// The portal's one script, loaded on every page. A static file on purpose: the portal's
// Content-Security-Policy allows no inline script. Everything here is an enhancement — each page works
// with JavaScript off.
document.addEventListener("DOMContentLoaded", () => {
  // Localize <time datetime="..."> into the reader's own locale and time zone. The server renders UTC
  // because it runs invariant-globalization and cannot know either. See WorkLedger.Server.Web.PortalFormat.
  for (const element of document.querySelectorAll("time[datetime]")) {
    const iso = element.getAttribute("datetime");
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      continue;
    }

    element.title = iso;
    element.textContent = date.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  }

  // Copy buttons for the one-time links. Rendered hidden, so nobody without JavaScript sees a button that
  // does nothing — the link sits in a read-only field they can select themselves.
  for (const button of document.querySelectorAll("button[data-copy]")) {
    const field = document.getElementById(button.dataset.copy);
    if (!field) {
      continue;
    }

    button.hidden = false;
    button.addEventListener("click", async () => {
      try {
        await navigator.clipboard.writeText(field.value);
        button.textContent = "Copied";
      } catch {
        field.select();
      }
    });
  }
});
