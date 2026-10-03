// Points the download buttons straight at the installers of the latest release.
// Without it (no JavaScript, GitHub unreachable) they lead to the releases page, which works too.
const latest = "https://api.github.com/repos/inowakowski/home-assistant-desktop-app/releases/latest";

fetch(latest)
  .then((response) => (response.ok ? response.json() : Promise.reject(response.status)))
  .then((release) => {
    for (const link of document.querySelectorAll("[data-asset]")) {
      const asset = release.assets.find((a) => a.name.endsWith(link.dataset.asset));
      if (asset) link.href = asset.browser_download_url;
    }
    for (const label of document.querySelectorAll("[data-version]")) {
      label.textContent = release.tag_name.replace(/^v/, "");
    }
  })
  .catch(() => {});
