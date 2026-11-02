// Fills in the latest release (version, date, sizes, direct links) and the star count from GitHub's public API.
// Every link already works without this script: they point at releases/latest/download/<name>.
(function () {
  "use strict";

  var REPO = "usmanmateen/untrail";
  var API = "https://api.github.com/repos/" + REPO;
  var CACHE_MS = 10 * 60 * 1000;

  function cached(key, url) {
    try {
      var hit = JSON.parse(sessionStorage.getItem(key) || "null");
      if (hit && Date.now() - hit.at < CACHE_MS) return Promise.resolve(hit.data);
    } catch (e) { /* storage unavailable: fetch instead */ }
    return fetch(url, { headers: { Accept: "application/vnd.github+json" } })
      .then(function (r) { if (!r.ok) throw new Error(r.status); return r.json(); })
      .then(function (data) {
        try { sessionStorage.setItem(key, JSON.stringify({ at: Date.now(), data: data })); } catch (e) { /* ignore */ }
        return data;
      });
  }

  function size(bytes) {
    return bytes >= 1048576 ? (bytes / 1048576).toFixed(1) + " MB" : Math.round(bytes / 1024) + " KB";
  }

  function date(iso) {
    try { return new Date(iso).toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" }); }
    catch (e) { return iso.slice(0, 10); }
  }

  function each(sel, fn) { Array.prototype.forEach.call(document.querySelectorAll(sel), fn); }

  cached("untrail-release", API + "/releases/latest").then(function (rel) {
    var assets = {};
    (rel.assets || []).forEach(function (a) { assets[a.name] = a; });
    var version = (rel.tag_name || "").replace(/^v/, "");

    // Older releases have no UntrailSetup.zip: send those buttons to the .exe instead.
    each("[data-dl]", function (a) {
      var name = a.getAttribute("data-dl");
      var asset = assets[name] || (name === "UntrailSetup.zip" && assets["UntrailSetup.exe"]);
      if (asset) a.href = asset.browser_download_url;
    });
    each("[data-size]", function (p) {
      var asset = assets[p.getAttribute("data-size")];
      if (asset) p.textContent = "Version " + version + " · " + size(asset.size);
    });

    var portable = (rel.assets || []).filter(function (a) { return /-portable\.zip$/.test(a.name); })[0];
    if (portable) {
      each("[data-portable]", function (a) { a.href = portable.browser_download_url; });
      each("[data-size-portable]", function (p) { p.textContent = "Version " + version + " · " + size(portable.size); });
    }
    if (assets["SHA256SUMS.txt"]) each("[data-sums]", function (a) { a.href = assets["SHA256SUMS.txt"].browser_download_url; });

    var line = document.querySelector("[data-release-line]");
    if (line && version) line.textContent = "Version " + version + " · Installer (.zip) · No admin rights needed · MIT licence";
    var sub = document.querySelector("[data-release-sub]");
    if (sub && version) sub.textContent = "Version " + version + ", released " + date(rel.published_at) + ". For Windows 10 and 11.";
  }).catch(function () { /* offline or rate-limited: the static links still work */ });

  cached("untrail-repo", API).then(function (repo) {
    if (typeof repo.stargazers_count !== "number") return;
    each("[data-stars]", function (s) { s.textContent = repo.stargazers_count.toLocaleString(); s.hidden = false; });
  }).catch(function () { /* ignore */ });

  // Respect reduced motion: no autoplaying demo.
  if (window.matchMedia && window.matchMedia("(prefers-reduced-motion: reduce)").matches) {
    each("video[autoplay]", function (v) { v.removeAttribute("autoplay"); v.pause(); v.controls = true; });
  }
})();
