// Palwyn landing page. No scroll listeners: IntersectionObserver and pointer events only.
const reduce = matchMedia("(prefers-reduced-motion: reduce)").matches;

// Solid nav once the page leaves the very top.
const nav = document.getElementById("nav");
const sentinel = document.createElement("div");
sentinel.style.cssText = "position:absolute;top:0;height:8px;width:1px";
document.body.prepend(sentinel);
new IntersectionObserver(([e]) => nav.classList.toggle("solid", !e.isIntersecting)).observe(sentinel);

// Light by default; the visitor's choice is remembered in this browser only.
const root = document.documentElement;
const themeBtn = document.getElementById("theme");
const themeMeta = document.querySelector('meta[name="theme-color"]');
function showTheme() {
  const dark = root.dataset.theme === "dark";
  themeBtn.setAttribute("aria-label", dark ? "Switch to light mode" : "Switch to dark mode");
  themeMeta.content = dark ? "#0c0c0e" : "#f7f7f8";
}
themeBtn.addEventListener("click", () => {
  const next = root.dataset.theme === "dark" ? "light" : "dark";
  root.dataset.theme = next;
  try { localStorage.setItem("theme", next); } catch (e) {}
  showTheme();
});
showTheme();

// One-shot reveals (bento cells, the privacy mark).
const reveal = new IntersectionObserver((entries) => {
  for (const e of entries) if (e.isIntersecting) { e.target.classList.add("in"); reveal.unobserve(e.target); }
}, { threshold: 0.25 });
document.querySelectorAll("[data-reveal], .bridge").forEach((el) => reveal.observe(el));

// Typing effect: fills .typed from data-type once it's visible.
function type(el) {
  if (el.dataset.done) return;
  el.dataset.done = "1";
  const text = el.dataset.type;
  if (reduce) { el.textContent = text; return; }
  let i = 0;
  const tick = () => { el.textContent = text.slice(0, ++i); if (i < text.length) setTimeout(tick, 55 + Math.random() * 60); };
  setTimeout(tick, 400);
}

// Hero: toast slides in, then the reply types itself.
const scene = document.getElementById("scene");
scene.classList.add("live");
setTimeout(() => type(scene.querySelector(".typed")), reduce ? 0 : 1800);

// Hero: gentle tilt toward the pointer (desktop only).
if (!reduce && matchMedia("(pointer: fine)").matches) {
  let raf = 0;
  addEventListener("pointermove", (e) => {
    cancelAnimationFrame(raf);
    raf = requestAnimationFrame(() => {
      scene.style.setProperty("--rx", `${((e.clientX / innerWidth) - 0.5) * 6}deg`);
      scene.style.setProperty("--ry", `${((e.clientY / innerHeight) - 0.5) * -4}deg`);
    });
  }, { passive: true });
}

// Story: the step in the middle of the viewport drives the sticky stage.
const stage = document.getElementById("stage");
const steps = [...document.querySelectorAll(".step")];
const stepObserver = new IntersectionObserver((entries) => {
  for (const e of entries) {
    if (!e.isIntersecting) continue;
    stage.dataset.step = e.target.dataset.step;
    steps.forEach((s) => s.classList.toggle("on", s === e.target));
    if (e.target.dataset.step === "2") type(stage.querySelector(".compose .typed"));
  }
}, { rootMargin: "-45% 0px -45% 0px" });
steps.forEach((s) => stepObserver.observe(s));
steps[0].classList.add("on");

// Ko-fi tip dialog. The iframe is only created on the first click, so nothing from ko-fi.com loads before that.
// Without JS (or <dialog>) the triggers stay plain links to the Ko-fi page.
const tip = document.getElementById("tip");
document.querySelectorAll("[data-tip]").forEach((a) => a.addEventListener("click", (e) => {
  if (!tip.showModal) return;
  e.preventDefault();
  const slot = tip.querySelector(".tip-frame");
  if (!slot.firstChild) {
    const f = document.createElement("iframe");
    f.src = "https://ko-fi.com/mael22/?hidefeed=true&widget=true&embed=true&preview=true";
    f.title = "Support Palwyn on Ko-fi";
    slot.append(f);
  }
  tip.showModal();
}));
tip.addEventListener("click", (e) => { if (e.target === tip) tip.close(); }); // backdrop click
