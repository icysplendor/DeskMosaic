import test from "node:test";
import assert from "node:assert/strict";
import { createDAVRequest } from "../src/dav-client.mjs";

test("an exhausted short retry budget returns control without another network attempt", async () => {
  let calls = 0;
  const request = createDAVRequest(
    async () => {
      calls++;
      throw Error("timed out");
    },
    {
      retryBudget: 0,
      sleep: () => assert.fail("budget already spent"),
    },
  );
  await assert.rejects(
    request("PUT", "books/n/main.md", "data", { "If-Match": '"1"' }),
    { code: "SYNC_RETRY" },
  );
  assert.equal(calls, 1);
});

test("423 retries with backoff and preserves conditional write headers", async () => {
  const calls = [],
    delays = [],
    progress = [];
  const headers = { "If-Match": '"original"' };
  const request = createDAVRequest(
    async (...args) => {
      calls.push(args);
      return { status: calls.length < 4 ? 423 : 204 };
    },
    {
      random: () => 0,
      sleep: async (ms) => delays.push(ms),
      onRetry: (event) => progress.push(event),
    },
  );
  assert.equal(
    (await request("PUT", "books/n/main.md", "original bytes", headers)).status,
    204,
  );
  assert.deepEqual(delays, [1000, 2000, 4000]);
  assert(
    calls.every((call) => call[2] === "original bytes" && call[3] === headers),
  );
  assert(
    progress.every((event) => event.phase === "retry" && event.status === 423),
  );
});
test("persistent server lock has its own error code, never a conflict", async () => {
  let calls = 0;
  const request = createDAVRequest(
    async () => {
      calls++;
      return { status: 423 };
    },
    { sleep: async () => {} },
  );
  await assert.rejects(
    request("DELETE", "books/n/main.md", null, { "If-Match": '"1"' }),
    { code: "DAV_LOCKED", status: 423, path: "books/n/main.md" },
  );
  assert.equal(calls, 4);
});
test("authentication, permission and quota failures do not retry", async () => {
  for (const status of [401, 403, 507]) {
    let calls = 0;
    const request = createDAVRequest(async () => {
      calls++;
      return { status };
    });
    await assert.rejects(request("GET", "books/n/main.md"), {
      status,
      code: "DAV_ERROR",
    });
    assert.equal(calls, 1);
  }
});
test("412 and 404 go to reconciliation without blind retries", async () => {
  for (const status of [412, 404]) {
    let calls = 0;
    const request = createDAVRequest(async () => {
      calls++;
      return { status };
    });
    assert.equal((await request("GET", "books/n/main.md")).status, status);
    assert.equal(calls, 1);
  }
});
test("Retry-After is honored; long waits are deferred to the scheduler", async () => {
  const delays = [];
  let calls = 0;
  const request = createDAVRequest(
    async () => ({ status: ++calls === 1 ? 429 : 200, retryAfter: "7" }),
    { sleep: async (ms) => delays.push(ms), random: () => 0 },
  );
  await request("GET", "books/n/main.md");
  assert.deepEqual(delays, [7000]);
  const long = createDAVRequest(
    async () => ({ status: 503, retryAfter: "120" }),
    { sleep: () => assert.fail("must release queue during long wait") },
  );
  await assert.rejects(long("GET", "books/n/main.md"), {
    code: "SYNC_RETRY",
    retryAfter: 120000,
  });
});
test("network failure retries; lease validation runs before the retry", async () => {
  let calls = 0,
    valid = true;
  const request = createDAVRequest(
    async () => {
      calls++;
      throw Error("connection dropped");
    },
    {
      sleep: async () => {
        valid = false;
      },
      beforeAttempt: () => {
        if (!valid)
          throw Object.assign(Error("lease expired"), {
            code: "SYNC_LEASE_LOST",
          });
      },
    },
  );
  await assert.rejects(
    request("PUT", "books/n/main.md", "bytes", { "If-Match": '"1"' }),
    { code: "SYNC_LEASE_LOST" },
  );
  assert.equal(calls, 1);
});
