package dev.palwyn.remote

import org.junit.Assert.assertEquals
import org.junit.Test

/** Live typing sends only what changed: backspaces for what's gone, then the new text. */
class RemoteDiffTest {
    @Test fun typing() = assertEquals(0 to "o", Remote.diff("hell", "hello"))
    @Test fun deleting() = assertEquals(2 to "", Remote.diff("hello", "hel"))
    @Test fun autocorrect() = assertEquals(2 to "he ", Remote.diff("I teh", "I the ")) // keeps the shared "I t"
    @Test fun cleared() = assertEquals(5 to "", Remote.diff("hello", ""))
    @Test fun unchanged() = assertEquals(0 to "", Remote.diff("same", "same"))
}
