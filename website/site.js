// Points the download buttons straight at the files of the latest release.
// Without it (no JavaScript, GitHub unreachable) they lead to the releases page, which works too.
// A button names its file without "-untested", which the Linux and macOS packages carry until somebody
// has tried them by hand, so that it still finds the file once the word is dropped.
const latest = "https://api.github.com/repos/inowakowski/home-assistant-desktop-app/releases/latest";

fetch(latest)
  .then((response) => (response.ok ? response.json() : Promise.reject(response.status)))
  .then((release) => {
    for (const link of document.querySelectorAll("[data-asset]")) {
      const asset = release.assets.find((a) => a.name.replace("-untested", "").endsWith(link.dataset.asset));
      if (asset) link.href = asset.browser_download_url;
    }
    for (const label of document.querySelectorAll("[data-version]")) {
      label.textContent = release.tag_name.replace(/^v/, "");
    }
  })
  .catch(() => {});
