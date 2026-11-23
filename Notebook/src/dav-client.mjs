// A 423 is a server lock, not an ETag conflict. Retry only transient failures;
// never remove version conditions or the server's locks to make a write succeed.
const transient = new Set([408, 423, 429, 500, 502, 503, 504]);
export function davError(status, method, path, retryAfter = 0) {
  const message =
    status === 423
      ? "网盘文件暂时被锁定，将自动重试"
      : status === 401
        ? "WebDAV 身份验证失败，请检查应用密码"
        : status === 403
          ? "WebDAV 没有该文件的访问权限"
          : status === 507
            ? "网盘剩余空间不足，本地修改已保留"
            : transient.has(status)
              ? "网盘暂时不可用，将自动重试"
              : `WebDAV 请求失败`;
  return Object.assign(
    new Error(`${message}（${method} ${path || "笔记库"}，${status}）`),
    {
      code:
        status === 423
          ? "DAV_LOCKED"
          : transient.has(status)
            ? "SYNC_RETRY"
            : "DAV_ERROR",
      status,
      method,
      path,
      retryAfter,
    },
  );
}
export function createDAVRequest(
  send,
  {
    retries = 3,
    retryBudget = 15000,
    sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms)),
    random = Math.random,
    beforeAttempt = () => {},
    onRetry = () => {},
    onResponse = () => {},
  } = {},
) {
  return async (method, path, data = null, headers = {}) => {
    const began = performance.now();
    for (let attempt = 0; ; attempt++) {
      await beforeAttempt({ method, path, attempt });
      let result, failure;
      const started = Date.now();
      try {
        result = await send(method, path, data, headers);
      } catch (error) {
        if (error.status && !transient.has(error.status)) throw error;
        failure = Object.assign(
          new Error("同步网络中断，本地修改已保留，将自动重试"),
          {
            code: "SYNC_RETRY",
            method,
            path,
            status: error.status || 0,
            cause: error,
          },
        );
      }
      onResponse({
        method,
        path,
        status: result?.status || failure?.status || 0,
        attempt,
        duration: Date.now() - started,
      });
      if (result && !transient.has(result.status)) {
        if (
          result.status >= 400 &&
          ![404, 405, 409, 412].includes(result.status)
        )
          throw davError(result.status, method, path);
        return result;
      }
      const raw = result?.retryAfter || "";
      const retryAfter = /^\d+$/.test(raw)
        ? Number(raw) * 1000
        : Math.max(0, Date.parse(raw) - Date.now()) || 0;
      failure ||= davError(result.status, method, path, retryAfter);
      if (attempt >= retries || retryAfter > 30000) throw failure;
      const delay = Math.max(
        retryAfter,
        1000 * 2 ** attempt * (1 + random() * 0.25),
      );
      // A slow network timeout already used the short retry budget. Release
      // the local queue and let the scheduler retry a fresh pass instead of
      // multiplying a 60-second transport timeout by four.
      if (performance.now() - began + delay > retryBudget) throw failure;
      onRetry({
        phase: "retry",
        method,
        path,
        status: failure.status,
        attempt: attempt + 1,
        retries,
        delay,
      });
      await sleep(delay);
    }
  };
}
