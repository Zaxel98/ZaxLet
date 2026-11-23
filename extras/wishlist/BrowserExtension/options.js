const api = typeof browser !== "undefined" ? browser : chrome;   // Firefox: browser.* (promise); Chrome/Edge/Brave: chrome.*
const $ = id => document.getElementById(id);
const SITES = { origins: ["http://*/*", "https://*/*"] };

api.storage.local.get({ token: "", enabled: true }).then(v => { $("token").value = v.token; $("enabled").checked = v.enabled; });

// Firefox treats site permissions as optional: without this permission the extension cannot read product pages.
async function showPermission() {
  const ok = await api.permissions.contains(SITES);
  $("perm").style.display = ok ? "none" : "block";
}
showPermission();
$("grant").addEventListener("click", async () => { await api.permissions.request(SITES); showPermission(); });   // must be triggered by a user click

$("diag").addEventListener("click", async () => {
  const out = $("diagout"); out.textContent = "Menguji...";
  const d = await api.runtime.sendMessage({ type: "diagnose" });
  const L = [];
  if (d.fatal) L.push("Galat: " + d.fatal);
  L.push("Token tersimpan: " + (d.token ? "ya" : "TIDAK (isi token di atas)"));
  L.push("Izin akses situs: " + (d.perm ? "ya" : "TIDAK (klik Izinkan akses ke situs)"));
  L.push("Sambungan ke ZaxLet: " + (d.ping === 200 ? "OK" : d.ping === 401 ? "token salah" : d.ping ? "kode " + d.ping : "GAGAL: " + (d.pingError || "?")));
  if (d.url) L.push("Tab dites: " + d.url);
  if (d.page) L.push(d.page);
  if (d.scanError) L.push("Gagal membaca halaman: " + d.scanError);
  if (d.scan) {
    if (d.scan.ok) L.push("Terbaca sebagai produk: " + d.scan.title + "\nHarga: " + (d.scan.price ? d.scan.price + " " + (d.scan.currency || "") : d.scan.priceText || "(tidak ada)") + "\nGambar: " + (d.scan.image ? (d.imageOk ? "terunduh" : "ada tapi gagal diunduh") : "(tidak ada)"));
    else L.push("Bukan produk: " + d.scan.why);
  }
  out.textContent = L.join("\n");
});

$("save").addEventListener("click", async () => {
  await api.storage.local.set({ token: $("token").value.trim(), enabled: $("enabled").checked });
  const st = $("status");
  st.className = ""; st.textContent = "Menguji...";
  const r = await api.runtime.sendMessage({ type: "ping" });
  if (r && r.ok) { st.className = "ok"; st.textContent = "Tersambung ke ZaxLet."; }
  else if (r && r.status === 401) { st.className = "bad"; st.textContent = "Token salah. Salin lagi dari ZaxLet."; }
  else { st.className = "bad"; st.textContent = "ZaxLet tidak menjawab" + (r && r.error ? " (" + r.error + ")" : "") + ". Pastikan ZaxLet berjalan dan \"Terima info produk dari ekstensi\" menyala."; }
});
