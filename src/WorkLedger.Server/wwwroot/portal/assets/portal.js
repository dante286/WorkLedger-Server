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

  // Invite and reset links carry their token in the URL fragment, which the browser never sends to the
  // server — so loading the page cannot spend it, and it cannot reach any log. Copy it into the form
  // field that asks for it (the POST is what spends it), then take it out of the address bar so it is
  // not left in history or on a shared screen. The field also takes a pasted code, for when this script
  // does not run or the link was mangled in transit.
  const fragment = new URLSearchParams(window.location.hash.slice(1));
  const fragmentFields = document.querySelectorAll("input[data-fragment]");
  for (const input of fragmentFields) {
    const value = fragment.get(input.dataset.fragment);
    if (value && !input.value) {
      input.value = value;
    }
  }

  if (fragmentFields.length > 0 && window.location.hash) {
    history.replaceState(null, "", window.location.pathname + window.location.search);
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
