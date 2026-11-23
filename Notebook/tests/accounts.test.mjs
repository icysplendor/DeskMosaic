import test from "node:test";
import assert from "node:assert/strict";
import { mkdtemp, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { Accounts } from "../server/accounts.mjs";

test("SQLite invitations expire, rotate and can only be consumed once concurrently", async () => {
  const dir = await mkdtemp(tmpdir() + "/notes-accounts-");
  let db = new Accounts(dir + "/accounts.sqlite");
  try {
    const old = db.invite(),
      token = db.invite();
    await assert.rejects(db.setup(old, "me", "independent-password"), {
      status: 403,
    });
    await assert.rejects(db.setup(token, "me", "short"), { status: 400 });
    await assert.rejects(
      db.setup(token, "invalid name", "independent-password"),
      { status: 400 },
    );
    const attempts = await Promise.allSettled([
      db.setup(token, "me", "independent-password"),
      db.setup(token, "me", "independent-password"),
    ]);
    assert.equal(attempts.filter((x) => x.status === "fulfilled").length, 1);
    assert.equal(await db.verify("other", "independent-password"), null);
    assert.equal(await db.verify("me", "wrong"), null);
    const before = await db.verify("me", "independent-password");
    assert(before);
    db.close();
    db = new Accounts(dir + "/accounts.sqlite");
    assert.deepEqual(await db.verify("me", "independent-password"), before);
    const expired = db.invite();
    db.db.prepare("UPDATE invitation SET expires=0").run();
    await assert.rejects(db.setup(expired, "me", "replacement-password"), {
      status: 403,
    });
    const newIdentity = await db.setup(
      db.invite(),
      "me",
      "replacement-password",
    );
    assert.notEqual(newIdentity.version, before.version);
    assert.equal(await db.verify("me", "independent-password"), null);
    assert.deepEqual(
      await db.verify("me", "replacement-password"),
      newIdentity,
    );
  } finally {
    db.close();
    await rm(dir, { recursive: true, force: true });
  }
});
