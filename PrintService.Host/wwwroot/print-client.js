// Framework-independent client: import it from React or any other browser application.
export function createPrintClient({ systemId, profileId = "default", agentUrl = "http://127.0.0.1:5055", storage, fetchImpl = globalThis.fetch } = {}) {
  const scope = String(systemId || "").trim().toLowerCase();
  if (!/^[a-z0-9._-]{1,64}$/.test(scope)) throw new Error("معرف التطبيق غير صالح");
  const base = agentUrl.replace(/\/+$/, "");
  if (storage === undefined) {
    try { storage = globalThis.localStorage; } catch { }
  }
  // If persistent browser storage is unavailable, keep preferences for this client session.
  const memory = new Map();
  const store = storage || { getItem: key => memory.get(key) ?? null, setItem: (key, value) => memory.set(key, value) };
  const deviceKey = "printservice:browser-device:v1";
  let deviceId = store.getItem(deviceKey);
  if (!deviceId) {
    deviceId = globalThis.crypto.randomUUID();
    store.setItem(deviceKey, deviceId);
  }
  const settingsKey = "printservice:settings:v1:" + JSON.stringify([base, scope, deviceId, profileId]);
  const clone = value => value == null ? value : JSON.parse(JSON.stringify(value, (_, item) => {
    if (typeof item === "number" && !Number.isFinite(item)) throw new Error("قيم خيارات الطباعة غير صالحة");
    return item;
  }));

  function getSettings() {
    try {
      const saved = JSON.parse(store.getItem(settingsKey));
      if (!saved || typeof saved !== "object" || Array.isArray(saved)) return {};
      return { printerName: saved.printerName ?? null, copies: saved.copies ?? 1, page: clone(saved.page ?? null) };
    } catch { return {}; }
  }

  function saveSettings({ printerName = null, copies = 1, page = null } = {}) {
    const saved = { printerName, copies, page: clone(page) };
    store.setItem(settingsKey, JSON.stringify(saved));
    return clone(saved);
  }

  function createJob({ jobId, documentType, contentBase64, printerName, copies, jobTitle = "SilentPrintJob", page } = {}) {
    if (typeof jobId !== "string" || !jobId.trim() || jobId.length > 128) throw new Error("معرف المهمة مطلوب");
    const defaults = getSettings();
    const job = {
      systemId: scope, jobId, documentType, contentBase64, jobTitle,
      printerName: printerName === undefined ? defaults.printerName ?? null : printerName,
      copies: copies === undefined ? defaults.copies ?? 1 : copies,
      page: clone(page === undefined ? defaults.page ?? null : page)
    };
    if (job.page?.margins) Object.freeze(job.page.margins);
    if (job.page) Object.freeze(job.page);
    return Object.freeze(job);
  }

  function createTextJob({ text, ...settings } = {}) {
    const bytes = new TextEncoder().encode(text ?? "");
    let binary = "";
    for (const byte of bytes) binary += String.fromCharCode(byte);
    return createJob({ ...settings, documentType: 3, contentBase64: btoa(binary) });
  }

  async function readResponse(response) {
    const result = await response.json();
    if (!response.ok) {
      const error = new Error(result.message || `PrintService HTTP ${response.status}`);
      error.status = response.status;
      throw error;
    }
    return result;
  }

  return Object.freeze({
    systemId: scope, deviceId, getSettings, saveSettings, createJob, createTextJob,
    getInfo: async () => readResponse(await fetchImpl(`${base}/api`)),
    getPrinters: async () => readResponse(await fetchImpl(`${base}/api/printers`)),
    getJob: async (jobId, { signal } = {}) => {
      const response = await fetchImpl(`${base}/api/jobs/${encodeURIComponent(jobId)}?systemId=${encodeURIComponent(scope)}`, { signal });
      return response.status === 404 ? null : readResponse(response);
    },
    print: async (job, { signal } = {}) => {
      if (job?.systemId !== scope) throw new Error("المهمة تخص تطبيقاً مختلفاً");
      const response = await fetchImpl(`${base}/api/print`, {
        method: "POST", headers: { "Content-Type": "application/json" }, body: JSON.stringify(job), signal
      });
      const result = await response.json();
      // Keep structured Failed/Unknown/Conflict results so the UI can decide what to display.
      if (typeof result.success !== "boolean" || typeof result.status !== "string")
        throw new Error(result.message || `PrintService HTTP ${response.status}`);
      return { ...result, httpStatus: response.status };
    }
  });
}
