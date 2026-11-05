const { chromium } = require("playwright");
const sites = process.argv.slice(2);
(async () => {
  const b = await chromium.launch();
  for (const url of sites) {
    const p = await (await b.newContext()).newPage();
    const issues = [];
    p.on("pageerror", e => issues.push("error " + e.message.slice(0, 120)));
    p.on("console", m => { if (m.type() === "error") issues.push("console " + m.text().slice(0, 120)); });
    await p.addInitScript(() => { window.__csp = []; document.addEventListener("securitypolicyviolation", e => window.__csp.push(e.violatedDirective)); });
    const r = await p.goto(url, { timeout: 60000 });
    await p.waitForTimeout(8000);
    const csp = await p.evaluate(() => window.__csp);
    const text = (await p.evaluate(() => document.body.innerText)).replace(/\s+/g, " ").slice(0, 110);
    const h = r.headers();
    console.log(`${r.status()} ${url}\n   csp:${!!h["content-security-policy"]} frame:${h["x-frame-options"]} | ${text}\n   issues: ${[...issues, ...csp].join(" | ") || "none"}`);
  }
  await b.close();
})();
