const api = typeof browser !== "undefined" ? browser : chrome;   // Firefox: browser.* (promise); Chrome/Edge/Brave: chrome.*
// ZaxLet Wishlist: reads product pages in the owner's browser and sends them to ZaxLet at 127.0.0.1.
// No continuous scanning: only when a page finishes loading (or changes URL in a single-page app),
// and only pages that look like a product are sent. Data never leaves this computer.
const BASE = "http://127.0.0.1:47892";
const timers = new Map();
const sentAt = new Map();          // url -> last sent time, so the same page is not sent repeatedly
let downUntil = 0;                 // ZaxLet not running: do not retry until this time

api.action.onClicked.addListener(() => api.runtime.openOptionsPage());

function schedule(tabId, url) {
  if (!/^https?:/i.test(url || "")) return;
  clearTimeout(timers.get(tabId));
  timers.set(tabId, setTimeout(() => run(tabId).catch(() => {}), 1800));   // give JavaScript pages time to render the price
}
api.webNavigation.onCompleted.addListener(d => { if (d.frameId === 0) schedule(d.tabId, d.url); });
api.webNavigation.onHistoryStateUpdated.addListener(d => { if (d.frameId === 0) schedule(d.tabId, d.url); });
api.tabs.onRemoved.addListener(id => { clearTimeout(timers.get(id)); timers.delete(id); });

// Executed INSIDE the page (must be self-contained, with no references outside this function).
function scanPage() {
  const meta = n => { const e = document.querySelector(`meta[property="${n}"],meta[name="${n}"],meta[itemprop="${n}"]`); return e ? (e.content || "").trim() : ""; };
  const prods = [];
  document.querySelectorAll('script[type="application/ld+json"]').forEach(s => {
    try {
      const walk = o => {
        if (!o || typeof o !== "object") return;
        if (Array.isArray(o)) { o.forEach(walk); return; }
        const t = o["@type"];
        if ((Array.isArray(t) ? t : [t]).includes("Product")) prods.push(o);
        if (o["@graph"]) walk(o["@graph"]);
      };
      walk(JSON.parse(s.textContent));
    } catch (e) { /* malformed JSON-LD: skip */ }
  });
  const p = prods[0];
  let title = "", price = "", currency = "", image = "";
  if (p) {
    title = p.name || "";
    const im = p.image; image = (Array.isArray(im) ? im[0] : im) || ""; if (image && typeof image === "object") image = image.url || "";
    let o = p.offers; if (Array.isArray(o)) o = o[0];
    if (o) { price = String(o.price ?? o.lowPrice ?? ""); currency = o.priceCurrency || ""; }
  }
  if (!title) title = meta("og:title");
  if (!image) image = meta("og:image");
  if (!price) { price = meta("product:price:amount") || meta("og:price:amount"); currency = currency || meta("product:price:currency") || meta("og:price:currency"); }
  const structured = !!p || meta("og:type") === "product" || !!meta("product:price:amount") || !!meta("og:price:amount");

  // Fallback for stores that render pages with JavaScript: largest "Rp..." text near the top of the page.
  let priceText = "";
  if (!price) {
    const re = /^(?:Rp\.?|IDR)\s?\d[\d.,]*(?:\s*[-–]\s*(?:Rp\.?|IDR)?\s?\d[\d.,]*)?$/i;
    let best = "", bestSize = 0, n = 0;
    for (const e of document.querySelectorAll("span,div,p,strong,b,h1,h2,h3,section")) {
      if (++n > 6000) break;
      if (e.childElementCount > 2) continue;
      const t = (e.textContent || "").trim();
      if (t.length > 40 || !re.test(t)) continue;
      const r = e.getBoundingClientRect();
      if (r.width === 0 || r.top > 1600 || r.bottom < 0) continue;
      const fs = parseFloat(getComputedStyle(e).fontSize) || 0;
      if (fs > bestSize) { best = t; bestSize = fs; }
    }
    priceText = best;
  }
  const buy = /keranjang|beli sekarang|beli langsung|add to cart|buy now|checkout/i.test(document.body ? document.body.innerText.slice(0, 20000) : "");
  if (!title) { const h = document.querySelector("h1"); title = h ? h.innerText.trim() : ""; }
  if (!title) title = document.title || "";   // tab title: stores like Shopee do not have a readable og:title/h1
  if (!image) {                               // largest visible image near the top of the page (product gallery)
    let bestImg = null, area = 0;
    for (const im of document.images) {
      const r = im.getBoundingClientRect();
      if (r.width < 150 || r.height < 150 || r.bottom < 0 || r.top > 1400) continue;
      if (r.width * r.height > area) { area = r.width * r.height; bestImg = im; }
    }
    if (bestImg) image = bestImg.currentSrc || bestImg.src || "";
  }
  const urlLooksProduct = /-i\.\d+\.\d+|\/products?\/|\/product-|\/p\/|\/items?\//i.test(location.pathname);   // common product URL patterns (Shopee, Lazada, Bukalapak, Kyou, ...)
  if (!title || !(structured || priceText && (buy || urlLooksProduct))) {   // not a product page (reason returned for the test button in settings)
    return { ok: false, why: !title ? "tidak ada judul halaman" : "terlihat bukan halaman produk (struktur produk: " + structured + ", teks harga: " + (priceText || price || "tidak ada") + ", tombol beli: " + buy + ")" };
  }
  try { if (image) image = new URL(image, location.href).href; } catch (e) { image = ""; }
  return { ok: true, url: location.href, title, price, currency, priceText, image };
}

async function toDataUrl(imgUrl) {
  try {
    const resp = await fetch(imgUrl);
    if (!resp.ok) return "";
    const blob = await resp.blob();
    if (blob.size > 12 * 1024 * 1024) return "";
    const bmp = await createImageBitmap(blob);
    const k = Math.min(1, 360 / Math.max(bmp.width, bmp.height));
    const w = Math.max(1, Math.round(bmp.width * k)), h = Math.max(1, Math.round(bmp.height * k));
    const cv = new OffscreenCanvas(w, h), ctx = cv.getContext("2d");
    ctx.fillStyle = "#fff"; ctx.fillRect(0, 0, w, h);   // white background: transparent images do not turn black
    ctx.drawImage(bmp, 0, 0, w, h);
    const out = await cv.convertToBlob({ type: "image/jpeg", quality: 0.82 });
    const buf = new Uint8Array(await out.arrayBuffer());
    let s = ""; for (let i = 0; i < buf.length; i += 0x8000) s += String.fromCharCode.apply(null, buf.subarray(i, i + 0x8000));
    return "data:image/jpeg;base64," + btoa(s);
  } catch (e) { return ""; }
}

async function post(path, token, body) {
  return fetch(BASE + path, { method: "POST", headers: { "Content-Type": "application/json", "X-ZaxLet-Token": token }, body: JSON.stringify(body || {}) });
}

async function run(tabId) {
  const { token, enabled } = await api.storage.local.get({ token: "", enabled: true });
  if (!enabled || !token || Date.now() < downUntil) return;
  // Firefox does not grant site permissions on install: without permission, reading pages and contacting ZaxLet fail (see options.html)
  if (!(await api.permissions.contains({ origins: ["http://*/*", "https://*/*"] }))) return;
  const tab = await api.tabs.get(tabId).catch(() => null);
  if (!tab || !/^https?:/i.test(tab.url || "")) return;
  const [res] = await api.scripting.executeScript({ target: { tabId }, func: scanPage });
  const info = res && res.result;
  if (!info || !info.ok) return;
  const last = sentAt.get(info.url);
  if (last && Date.now() - last < 10 * 60 * 1000) return;
  info.image = info.image ? await toDataUrl(info.image) : "";
  try {
    const r = await post("/product", token, info);
    if (r.ok) sentAt.set(info.url, Date.now());
    else if (r.status === 401) downUntil = Date.now() + 5 * 60 * 1000;   // wrong token: do not flood
  } catch (e) { downUntil = Date.now() + 60 * 1000; }   // ZaxLet not running
}

// Pages already open before the extension was installed, granted permission, or given a token will not fire navigation events:
// check active tabs now so they do not need to be reloaded.
const SITES = { origins: ["http://*/*", "https://*/*"] };
async function rescanActive() { for (const t of await api.tabs.query({ active: true })) schedule(t.id, t.url); }
api.runtime.onStartup.addListener(() => rescanActive().catch(() => {}));
api.runtime.onInstalled.addListener(() => rescanActive().catch(() => {}));
api.permissions.onAdded.addListener(() => { downUntil = 0; rescanActive().catch(() => {}); });
api.storage.onChanged.addListener(() => { downUntil = 0; rescanActive().catch(() => {}); });

// Test from the settings page: permission, token, connection to ZaxLet, and what was read from the most recently opened web tab.
async function diagnose() {
  const { token } = await api.storage.local.get({ token: "" });
  const out = { token: !!token, perm: await api.permissions.contains(SITES) };
  try { const r = await post("/ping", token, {}); out.ping = r.status; } catch (e) { out.pingError = String((e && e.message) || e); }
  const tabs = (await api.tabs.query({})).filter(t => /^https?:/i.test(t.url || "")).sort((a, b) => (b.lastAccessed || 0) - (a.lastAccessed || 0));
  if (!tabs.length) { out.page = "tidak ada tab web terbuka"; return out; }
  out.url = tabs[0].url;
  if (!out.perm) return out;
  try { const [res] = await api.scripting.executeScript({ target: { tabId: tabs[0].id }, func: scanPage }); out.scan = res && res.result; }
  catch (e) { out.scanError = String((e && e.message) || e); }
  if (out.scan && out.scan.ok && out.scan.image) out.imageOk = !!(await toDataUrl(out.scan.image));
  return out;
}

api.runtime.onMessage.addListener((msg, _s, reply) => {
  if (msg && msg.type === "ping") {
    api.storage.local.get({ token: "" }).then(({ token }) => post("/ping", token, {})).then(r => reply({ ok: r.ok, status: r.status })).catch(e => reply({ ok: false, status: 0, error: String((e && e.message) || e) }));
    return true;
  }
  if (msg && msg.type === "diagnose") { diagnose().then(reply).catch(e => reply({ fatal: String((e && e.message) || e) })); return true; }
});
