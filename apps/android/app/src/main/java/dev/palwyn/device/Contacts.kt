package dev.palwyn.device

import android.Manifest
import android.content.ContentProviderOperation
import android.content.ContentUris
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.ContactsContract
import android.provider.ContactsContract.CommonDataKinds.Email
import android.provider.ContactsContract.CommonDataKinds.Phone
import android.provider.ContactsContract.CommonDataKinds.StructuredName
import android.provider.ContactsContract.Data
import android.provider.ContactsContract.PhoneLookup
import android.provider.ContactsContract.RawContacts
import org.json.JSONArray
import org.json.JSONObject

/** The phone's contacts: names for numbers the PC already sees, and the contact list the PC asks for. */
object Contacts {
    private const val PAGE_BYTES = 800_000 // keeps a CONTACTS frame well under the 1 MiB limit
    private const val MAX_FIELDS = 20

    fun name(c: Context, number: String?): String? {
        if (number.isNullOrBlank() ||
            c.checkSelfPermission(Manifest.permission.READ_CONTACTS) != PackageManager.PERMISSION_GRANTED
        ) return null
        return try {
            c.contentResolver.query(
                Uri.withAppendedPath(PhoneLookup.CONTENT_FILTER_URI, Uri.encode(number)),
                arrayOf(PhoneLookup.DISPLAY_NAME), null, null, null,
            )?.use { if (it.moveToFirst()) it.getString(0) else null }
        } catch (e: RuntimeException) {
            null
        }
    }

    /**
     * Search match, the same rule as the PC's (PhoneContact.NumberMatches): [name] containing [query], or a
     * number containing its digits (3 or more). A leading 0 is a national prefix, so "0917" also finds
     * "+63 917 …", but only in numbers written with a country code.
     */
    fun matches(query: String, name: String?, numbers: List<String>): Boolean {
        if (name?.contains(query, ignoreCase = true) == true) return true
        val digits = query.filter { it in '0'..'9' }
        if (digits.length < 3) return false
        val core = digits.trimStart('0')
        return numbers.any { number ->
            val n = number.filter { it in '0'..'9' }
            n.contains(digits) ||
                (core.length >= 3 && core.length < digits.length && number.trimStart().startsWith('+') && n.contains(core))
        }
    }

    private class Entry(val name: String, val photo: Boolean) {
        val numbers = JSONArray()
        val emails = JSONArray()
        val seen = HashSet<String>()
    }

    /** CONTACT_PHOTO: the contact's thumbnail (about 96 px) as base64 JPEG, or null when it has none. Needs READ_CONTACTS. */
    fun photo(c: Context, id: Long): String? {
        val uri = ContentUris.withAppendedId(ContactsContract.Contacts.CONTENT_URI, id)
        val bytes = ContactsContract.Contacts.openContactPhotoInputStream(c.contentResolver, uri, false)?.use { it.readBytes() }
        return bytes?.let { android.util.Base64.encodeToString(it, android.util.Base64.NO_WRAP) }
    }

    /**
     * CONTACTS payload: contacts with a number or an email, by name, skipping [offset], at most [limit], and
     * fewer when the page would get too big (`more` says whether any are left). Needs READ_CONTACTS.
     */
    fun page(c: Context, offset: Int, limit: Int): JSONObject {
        // ponytail: reads the whole table for every page; fine for thousands of contacts, page in SQL if that gets slow.
        val all = LinkedHashMap<Long, Entry>()
        c.contentResolver.query(
            Data.CONTENT_URI,
            arrayOf(Data.CONTACT_ID, Data.DISPLAY_NAME_PRIMARY, Data.MIMETYPE, Data.DATA1, Phone.TYPE, Phone.LABEL, Phone.NORMALIZED_NUMBER,
                Data.PHOTO_THUMBNAIL_URI),
            "${Data.MIMETYPE} IN (?, ?)", arrayOf(Phone.CONTENT_ITEM_TYPE, Email.CONTENT_ITEM_TYPE),
            "${Data.DISPLAY_NAME_PRIMARY} COLLATE LOCALIZED, ${Data.CONTACT_ID}",
        )?.use { cur ->
            while (cur.moveToNext()) {
                val value = cur.getString(3)?.trim()?.takeIf { it.isNotEmpty() } ?: continue
                val e = all.getOrPut(cur.getLong(0)) {
                    Entry((cur.getString(1)?.takeIf { it.isNotBlank() } ?: value).take(128), cur.getString(7) != null)
                }
                if (cur.getString(2) == Phone.CONTENT_ITEM_TYPE) {
                    // The same number saved by several accounts shows once.
                    val key = cur.getString(6) ?: value.filter { it.isDigit() || it == '+' }
                    if (value.length > 64 || e.numbers.length() >= MAX_FIELDS || !e.seen.add(key)) continue
                    val label = Phone.getTypeLabel(c.resources, cur.getInt(4), cur.getString(5)).toString()
                    e.numbers.put(JSONObject().put("number", value).put("type", typeName(cur.getInt(4)))
                        .apply { if (label.isNotBlank()) put("label", label.take(64)) })
                } else {
                    if (value.length > 254 || e.emails.length() >= MAX_FIELDS || !e.seen.add(value.lowercase())) continue
                    e.emails.put(value)
                }
            }
        }
        val out = JSONArray()
        var bytes = 0
        var next = offset
        for ((id, e) in all.entries.drop(offset)) {
            val o = JSONObject().put("id", "$id").put("name", e.name).put("numbers", e.numbers).put("emails", e.emails)
                .apply { if (e.photo) put("photo", true) }
            bytes += o.toString().toByteArray().size
            if (out.length() >= limit || (bytes > PAGE_BYTES && out.length() > 0)) break
            out.put(o)
            next++
        }
        return JSONObject().put("contacts", out).put("more", next < all.size)
    }

    private fun typeName(type: Int) = when (type) {
        Phone.TYPE_MOBILE -> "mobile"
        Phone.TYPE_HOME -> "home"
        Phone.TYPE_WORK -> "work"
        else -> "other"
    }

    /**
     * CONTACT_SAVE: creates a contact, or replaces the name, numbers and emails of contact [id]. Returns the
     * contact id. New contacts go to the phone's own storage (no account), as Android does without a choice.
     * Needs WRITE_CONTACTS.
     */
    fun save(c: Context, id: Long?, p: JSONObject): Long {
        val name = p.getString("name").trim()
        require(name.isNotEmpty()) { "no name" }
        val r = c.contentResolver
        val ops = ArrayList<ContentProviderOperation>()
        var target: Long? = null
        if (id != null) {
            // An edited contact can be several raw contacts (phone, Google, SIM) shown as one. Its old name,
            // numbers and emails come off all of them and the new ones go on one, preferring the Google copy
            // so the edit syncs.
            // ponytail: an app's read-only raw contact (e.g. a messenger's) may re-add its data on its next sync.
            val raws = r.query(
                RawContacts.CONTENT_URI, arrayOf(RawContacts._ID, RawContacts.ACCOUNT_TYPE),
                "${RawContacts.CONTACT_ID} = ? AND ${RawContacts.DELETED} = 0", arrayOf("$id"), RawContacts._ID,
            )?.use { cur -> buildList { while (cur.moveToNext()) add(cur.getLong(0) to cur.getString(1)) } }.orEmpty()
            require(raws.isNotEmpty()) { "no such contact" }
            target = (raws.firstOrNull { it.second == "com.google" } ?: raws.first()).first
            ops += ContentProviderOperation.newDelete(Data.CONTENT_URI)
                .withSelection(
                    "${Data.RAW_CONTACT_ID} IN (${raws.joinToString { "${it.first}" }}) AND ${Data.MIMETYPE} IN (?, ?, ?)",
                    arrayOf(StructuredName.CONTENT_ITEM_TYPE, Phone.CONTENT_ITEM_TYPE, Email.CONTENT_ITEM_TYPE),
                ).build()
        } else {
            ops += ContentProviderOperation.newInsert(RawContacts.CONTENT_URI)
                .withValue(RawContacts.ACCOUNT_TYPE, null).withValue(RawContacts.ACCOUNT_NAME, null).build()
        }
        fun row(mime: String) = ContentProviderOperation.newInsert(Data.CONTENT_URI)
            .apply { if (target != null) withValue(Data.RAW_CONTACT_ID, target) else withValueBackReference(Data.RAW_CONTACT_ID, 0) }
            .withValue(Data.MIMETYPE, mime)
        ops += row(StructuredName.CONTENT_ITEM_TYPE).withValue(StructuredName.DISPLAY_NAME, name).build()
        val numbers = p.getJSONArray("numbers")
        for (i in 0 until minOf(numbers.length(), MAX_FIELDS)) {
            val n = numbers.getJSONObject(i)
            val number = n.getString("number").trim().takeIf { it.isNotEmpty() } ?: continue
            val label = n.optString("label").takeIf { it.isNotBlank() }
            val type = when (n.getString("type")) {
                "mobile" -> Phone.TYPE_MOBILE
                "home" -> Phone.TYPE_HOME
                "work" -> Phone.TYPE_WORK
                else -> if (label != null) Phone.TYPE_CUSTOM else Phone.TYPE_OTHER
            }
            ops += row(Phone.CONTENT_ITEM_TYPE).withValue(Phone.NUMBER, number).withValue(Phone.TYPE, type)
                .apply { if (type == Phone.TYPE_CUSTOM) withValue(Phone.LABEL, label) }.build()
        }
        val emails = p.getJSONArray("emails")
        for (i in 0 until minOf(emails.length(), MAX_FIELDS)) {
            val email = emails.getString(i).trim().takeIf { it.isNotEmpty() } ?: continue
            ops += row(Email.CONTENT_ITEM_TYPE).withValue(Email.ADDRESS, email).withValue(Email.TYPE, Email.TYPE_OTHER).build()
        }
        val results = r.applyBatch(ContactsContract.AUTHORITY, ops)
        val raw = target ?: ContentUris.parseId(results[0].uri!!)
        return r.query(RawContacts.CONTENT_URI, arrayOf(RawContacts.CONTACT_ID), "${RawContacts._ID} = ?", arrayOf("$raw"), null)
            ?.use { if (it.moveToFirst()) it.getLong(0) else null } ?: id ?: 0
    }

    /** CONTACT_DELETE: the contact and every raw contact behind it, so synced accounts delete it too. Needs WRITE_CONTACTS. */
    fun delete(c: Context, id: Long): Boolean =
        c.contentResolver.delete(ContentUris.withAppendedId(ContactsContract.Contacts.CONTENT_URI, id), null, null) > 0
}
