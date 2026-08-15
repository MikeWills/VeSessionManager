// Charts for the stats page (#63).
//
// The data arrives on a data- attribute rather than in an inline <script>, because this app's CSP is
// script-src 'self': an inline block renders and silently never runs. Same constraint that put
// Chart.js under wwwroot/lib instead of on a CDN.
(function () {
  "use strict";

  var host = document.getElementById("statsData");
  if (!host || typeof Chart === "undefined") return;

  var data;
  try {
    data = JSON.parse(host.getAttribute("data-stats"));
  } catch (e) {
    return;
  }

  if (!data || !data.labels || data.labels.length === 0) return;

  // Read from CSS custom properties so the charts follow the theme rather than hardcoding colours
  // that only look right in one of them — the app renders in light and dark (see theme.js).
  var styles = getComputedStyle(document.documentElement);
  function token(name, fallback) {
    var value = styles.getPropertyValue(name);
    return value && value.trim() ? value.trim() : fallback;
  }

  var ink = token("--ink", "#1a1a1a");
  var grid = token("--rule", "rgba(128,128,128,.25)");

  // Chart.js does not know about the theme, so the shared bits are set once here.
  Chart.defaults.color = ink;
  Chart.defaults.borderColor = grid;
  Chart.defaults.font.family = getComputedStyle(document.body).fontFamily;

  var palette = {
    sessions: "#2f6f4e",
    candidates: "#3d6f9e",
    passed: "#2f6f4e",
    failed: "#c0392b",
    newLicence: "#3d6f9e",
    upgrade: "#8e6cae"
  };

  function bar(canvasId, datasets, stacked) {
    var canvas = document.getElementById(canvasId);
    if (!canvas) return;

    new Chart(canvas, {
      type: "bar",
      data: { labels: data.labels, datasets: datasets },
      options: {
        responsive: true,
        maintainAspectRatio: false,
        scales: {
          x: { stacked: !!stacked, grid: { display: false } },
          // Whole people and whole sessions — a fractional tick would be meaningless here.
          y: { stacked: !!stacked, beginAtZero: true, ticks: { precision: 0 } }
        },
        plugins: { legend: { position: "bottom" } }
      }
    });
  }

  bar("volumeChart", [
    { label: "Sessions", data: data.sessions, backgroundColor: palette.sessions },
    { label: "Candidates tested", data: data.candidates, backgroundColor: palette.candidates }
  ]);

  bar("outcomeChart", [
    { label: "Passed", data: data.passed, backgroundColor: palette.passed },
    { label: "Failed", data: data.failed, backgroundColor: palette.failed }
  ], true);

  bar("licenceChart", [
    { label: "New licences", data: data.newLicences, backgroundColor: palette.newLicence },
    { label: "Upgrades", data: data.upgrades, backgroundColor: palette.upgrade }
  ], true);
})();
