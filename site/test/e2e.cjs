// The published site in Chromium, served with the production headers from vercel.json.
// Usage: node site/test/e2e.cjs <published wwwroot folder>
const { chromium, devices } = require("playwright");
const { spawn } = require("child_process");
const path = require("path");

const root = path.resolve(process.argv[2] || "out/wwwroot");
const repo = path.resolve(__dirname, "../..");
const port = 4300 + Math.floor(Math.random() * 500);
const base = `http://127.0.0.1:${port}/`;
let failures = 0;
const check = (ok, name, detail = "") => {
  console.log(`${ok ? "ok  " : "FAIL"} ${name}${!ok && detail ? "  " + detail : ""}`);
  if (!ok) failures++;
};
const squash = s => (s || "").replace(/\s+/g, " ").trim();
const header = "id,account,time,amount,currency,direction,channel,counterparty,counterparty_country,description\n";

(async () => {
  const server = spawn("python3", [path.join(__dirname, "serve.py"), process.env.VERCEL_JSON || path.join(repo, "vercel.json"), root, String(port)], { stdio: "inherit" });
  try {
    for (let i = 0; i < 50; i++) {
      try { if ((await fetch(base)).ok) break; } catch { /* starting */ }
      await new Promise(r => setTimeout(r, 100));
    }
    const head = await fetch(base);
    const csp = head.headers.get("content-security-policy") || "";
    check((csp.match(/script-src [^;]*/) || [""])[0] === "script-src 'self' 'wasm-unsafe-eval'" && csp.includes("connect-src 'self'") && csp.includes("frame-ancestors 'none'"), "strict Content-Security-Policy");
    check((await fetch(base + "_framework/blazor.webassembly.js")).headers.get("cache-control") === "no-cache", "the unfingerprinted loader is revalidated");

    const browser = await chromium.launch();
    const open = async (options = {}) => {
      const ctx = await browser.newContext({ viewport: { width: 1280, height: 900 }, ...options });
      const p = await ctx.newPage();
      const problems = [];
      p.on("pageerror", e => problems.push("error: " + e.message));
      p.on("dialog", d => { problems.push("dialog: " + d.message()); d.dismiss(); });
      p.on("console", m => { if (m.type() === "error") problems.push("console: " + m.text().slice(0, 200)); });
      p.on("request", r => { if (!r.url().startsWith(base)) problems.push("request: " + r.url()); });
      await p.addInitScript(() => { window.__csp = []; document.addEventListener("securitypolicyviolation", e => window.__csp.push(e.violatedDirective + " " + e.blockedURI)); });
      await p.goto(base);
      await p.waitForSelector("#run", { timeout: 60000 });
      const done = async () => [...problems, ...(await p.evaluate(() => window.__csp)).map(v => "csp: " + v)];
      return { ctx, p, done };
    };
    const results = async p => squash(await p.textContent(".results"));
    const upload = async (p, name, text) => {
      await p.setInputFiles("#file", { name, mimeType: "text/csv", buffer: Buffer.from(text) });
      await p.waitForFunction(() => !/Reading/.test(document.querySelector("#loaded").textContent), null, { timeout: 30000 });
    };
    const run = async p => {
      await p.click("#run");
      await p.waitForFunction(() => document.querySelector("#summary, #problems"), null, { timeout: 30000 });
      return results(p);
    };

    {
      const { ctx, p, done } = await open();
      await p.click("#sample");
      const loaded = squash(await p.textContent("#loaded"));
      check(loaded.includes("5,320 transactions on 49 accounts"), "the sample loads", loaded);
      let text = await run(p);
      check(text.includes("11 alerts") && text.includes("6 high") && text.includes("3 medium") && text.includes("2 low"), "the rules run over the sample", text.slice(0, 160));
      check(await p.locator(".alert").count() === 11 && await p.locator(".planted").count() === 11, "every alert is shown, with what was planted");
      check(text.includes("3 deposits by cash within 3 days") && text.includes("went out again within 33 hours"), "alerts explain themselves");
      await p.selectOption(".filter select", "High");
      check(await p.locator(".alert").count() === 6, "the severity filter works");
      await p.selectOption(".filter select", "Low");

      await p.fill("#rules", "{ not json");
      text = await run(p);
      check(text.includes("isn't valid JSON"), "broken rules are refused, saying why", text.slice(0, 120));
      await p.fill("#rules", '{ "rules": [ { "id": "x", "type": "script", "code": "alert(1)" }, { "id": "y", "type": "large" } ] }');
      text = await run(p);
      check(text.includes("isn't one of") && text.includes("needs amount"), "every problem with the rules is listed", text.slice(0, 200));
      await p.click("text=Reset to the example");

      await upload(p, "bad.csv", header + "t1,A1,2026-05-04T09:00:00,10,USD,in,cash,,,\n");
      text = await results(p);
      check(text.includes("Line 2") && text.includes("the time must"), "a bad file is refused by line", text.slice(0, 160));

      const hostile = header +
        't1,"<img src=x onerror=alert(1)>",2026-05-04T09:00:00Z,5000,USD,out,wire,"<script>alert(2)</script>\u202E evil",KP,\n' +
        't2,"=HYPERLINK(""http://x"")",2026-05-05T09:00:00Z,12000,USD,in,cash,,,\n';
      await upload(p, "<b>hostile</b>.csv", hostile);
      check(squash(await p.textContent("#loaded")).startsWith("<b>hostile</b>.csv: 2 transactions"), "a hostile file name stays text");
      text = await run(p);
      const injected = await p.evaluate(() => document.querySelectorAll(".results img, .results script, #loaded b").length);
      check(injected === 0 && text.includes("<img src=x onerror=alert(1)>") && text.includes('"<script>alert(2)</script> evil" in KP'), "hostile names stay text", text.slice(0, 300));
      check(!text.includes("\u202E"), "invisible characters are dropped from alerts");

      await upload(p, "big.csv", header + "x".repeat(21 * 1024 * 1024));
      text = await results(p);
      check(text.includes("larger than 20 MB"), "a file over 20 MB is refused", text.slice(0, 120));
      const issues = await done();
      check(issues.length === 0, "no errors, policy violations or outside requests", issues.join(" | "));
      await ctx.close();
    }

    {
      const { ctx, p, done } = await open({ ...devices["iPhone 13"] });
      await p.click("#sample");
      await run(p);
      const [scroll, width] = await p.evaluate(() => [document.documentElement.scrollWidth, innerWidth]);
      check(scroll <= width, "no sideways scrolling on a phone", `page ${scroll}px, screen ${width}px`);
      const spill = await p.evaluate(() => [".results", ".inputs"].flatMap(sel => {
        const card = document.querySelector(sel).getBoundingClientRect();
        return [...document.querySelectorAll(sel + " *")].filter(e => e.getBoundingClientRect().right > card.right + 1).map(e => e.className || e.tagName);
      }));
      check(spill.length === 0, "nothing spills out of the cards on a phone", spill.slice(0, 5).join(", "));
      check((await done()).length === 0, "phone: no errors");
      await ctx.close();
    }

    {
      const ctx = await browser.newContext();
      const p = await ctx.newPage();
      await p.setContent(`<iframe src="${base}" width="400" height="300"></iframe>`);
      await p.waitForTimeout(1500);
      const frame = p.frames().find(f => f !== p.mainFrame());
      const framed = frame ? await frame.evaluate(() => !!document.querySelector(".brand")).catch(() => false) : false;
      check(!framed, "the page can't be framed");
      await ctx.close();
    }
    await browser.close();
  } finally {
    server.kill();
  }
  console.log(failures ? `\n${failures} failed` : "\nall passed");
  process.exit(failures ? 1 : 0);
})().catch(e => { console.error(e); process.exit(1); });
