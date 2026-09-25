// Localizes <time datetime="..."> elements into the reader's own locale and time zone. The server
// renders them in UTC because it runs invariant-globalization and cannot know either; without this
// script the page still shows the correct time, in UTC. See WorkLedger.Server.Web.PortalFormat.
//
// A static file on purpose: the portal's Content-Security-Policy allows no inline script.
document.addEventListener("DOMContentLoaded", () => {
  for (const element of document.querySelectorAll("time[datetime]")) {
    const iso = element.getAttribute("datetime");
    const date = new Date(iso);
    if (Number.isNaN(date.getTime())) {
      continue;
    }

    element.title = iso;
    element.textContent = date.toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" });
  }
});
