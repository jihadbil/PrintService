import { test } from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";

// Load the exact browser module without introducing a build tool or framework dependency.
const source = await readFile(new URL("../PrintService.Host/wwwroot/print-client.js", import.meta.url), "utf8");
const { createPrintClient } = await import("data:text/javascript;base64," + Buffer.from(source).toString("base64"));
function storage() {
  const values = new Map();
  return { getItem: key => values.get(key) ?? null, setItem: (key, value) => values.set(key, value) };
}

test("preferences persist per application, local agent, device and profile", () => {
  const local = storage();
  const restaurant = createPrintClient({ systemId: "Restaurant", storage: local });
  restaurant.saveSettings({ printerName: "Kitchen", copies: 2, page: { paperWidthMm: 80, paperHeightMm: 200 } });
  assert.equal(createPrintClient({ systemId: "restaurant", storage: local }).getSettings().printerName, "Kitchen");
  assert.equal(createPrintClient({ systemId: "accounting", storage: local }).getSettings().printerName, undefined);
  assert.equal(createPrintClient({ systemId: "restaurant", profileId: "cashier", storage: local }).getSettings().printerName, undefined);
  assert.equal(createPrintClient({ systemId: "restaurant", agentUrl: "http://127.0.0.1:6060", storage: local }).getSettings().printerName, undefined);
  const otherDevice = createPrintClient({ systemId: "restaurant", storage: storage() });
  assert.notEqual(otherDevice.deviceId, restaurant.deviceId);
  assert.equal(otherDevice.getSettings().printerName, undefined);
});

test("saved application settings cannot change service engine or limits", () => {
  const client = createPrintClient({ systemId: "restaurant", storage: storage() });
  client.saveSettings({ printerName: "Kitchen", documentEngine: "Other", maxCopies: 1000 });
  assert.equal(client.getSettings().documentEngine, undefined);
  assert.equal(client.getSettings().maxCopies, undefined);
});

test("text requests preserve UTF-8 and immutable page settings", () => {
  const client = createPrintClient({ systemId: "restaurant", storage: storage() });
  const page = { paperWidthMm: 80, paperHeightMm: 200, margins: { leftMm: 2 } };
  client.saveSettings({ printerName: "Kitchen", copies: 2, page });
  page.margins.leftMm = 40;
  const job = client.createTextJob({ jobId: "invoice:123", text: "فاتورة 😀" });
  assert.equal(Buffer.from(job.contentBase64, "base64").toString("utf8"), "فاتورة 😀");
  assert.equal(job.systemId, "restaurant"); assert.equal(job.copies, 2); assert.equal(job.page.margins.leftMm, 2);
  assert.ok(Object.isFrozen(job)); assert.ok(Object.isFrozen(job.page)); assert.ok(Object.isFrozen(job.page.margins));
  assert.throws(() => { job.page.margins.leftMm = 5; }, TypeError);
});

test("retries send the same job and namespace without regenerating IDs", async () => {
  const requests = [];
  const client = createPrintClient({ systemId: "restaurant", storage: storage(), fetchImpl: async (url, options) => {
    requests.push({ url, body: options.body });
    return { status: 200, json: async () => ({ success: true, status: "Submitted", jobId: "invoice:123", systemId: "restaurant" }) };
  }});
  const job = client.createTextJob({ jobId: "invoice:123", text: "test" });
  await client.print(job); await client.print(job);
  assert.equal(requests[0].body, requests[1].body);
  assert.equal(JSON.parse(requests[0].body).jobId, "invoice:123");
});

test("lookup carries the application namespace", async () => {
  let target;
  const client = createPrintClient({ systemId: "accounting", storage: storage(), fetchImpl: async url => {
    target = url; return { status: 404 };
  }});
  assert.equal(await client.getJob("invoice:123"), null);
  assert.equal(target, "http://127.0.0.1:5055/api/jobs/invoice%3A123?systemId=accounting");
});

test("a client cannot accidentally submit another application's job", async () => {
  let calls = 0;
  const client = createPrintClient({ systemId: "accounting", storage: storage(), fetchImpl: async () => { calls++; } });
  await assert.rejects(client.print({ systemId: "restaurant" })); assert.equal(calls, 0);
});

test("explicit per-job overrides do not overwrite stored defaults", () => {
  const client = createPrintClient({ systemId: "accounting", storage: storage() });
  client.saveSettings({ printerName: "A", copies: 2, page: { paperName: "A4" } });
  const job = client.createTextJob({ jobId: "123", text: "x", printerName: "B", copies: 1, page: null });
  assert.equal(job.printerName, "B"); assert.equal(job.page, null); assert.equal(client.getSettings().printerName, "A");
});

test("invalid numeric layout cannot become null and silently use defaults", () => {
  const client = createPrintClient({ systemId: "restaurant", storage: storage() });
  assert.throws(() => client.saveSettings({ page: { paperWidthMm: NaN, paperHeightMm: 200 } }));
  assert.throws(() => client.createTextJob({ jobId: "123", text: "x", page: { margins: { leftMm: Infinity } } }));
});

test("uncertain results remain structured and invalid IDs are rejected", async () => {
  const client = createPrintClient({ systemId: "restaurant", storage: storage(), fetchImpl: async () => ({
    status: 503, json: async () => ({ success: false, status: "Unknown", message: "review" })
  }) });
  const result = await client.print(client.createTextJob({ jobId: "123", text: "test" }));
  assert.equal(result.status, "Unknown"); assert.equal(result.httpStatus, 503);
  assert.throws(() => createPrintClient({ systemId: "bad:scope", storage: storage() }));
  assert.throws(() => client.createTextJob({ text: "x" }));
});
