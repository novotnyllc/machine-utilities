import assert from "node:assert/strict";
import fs from "node:fs";
import path from "node:path";
import test from "node:test";
import { pathToFileURL } from "node:url";

const routerPath = process.env.AGENT_UTILITIES_MODEL_ROUTER;
const manifestPath = process.env.AGENT_UTILITIES_PLUGIN_MANIFEST;
assert.ok(routerPath && path.isAbsolute(routerPath), "set AGENT_UTILITIES_MODEL_ROUTER to the Agent Utilities model-routing.mjs path");
assert.ok(manifestPath && path.isAbsolute(manifestPath), "set AGENT_UTILITIES_PLUGIN_MANIFEST to the Agent Utilities Codex plugin manifest path");

const MINIMUM_AGENT_UTILITIES_VERSION = "0.5.10";
const resolvedRouterPath = fs.realpathSync(routerPath);
const resolvedManifestPath = fs.realpathSync(manifestPath);
const expectedRouterPath = fs.realpathSync(path.resolve(path.dirname(resolvedManifestPath), "..", "scripts", "model-routing.mjs"));
assert.equal(resolvedRouterPath, expectedRouterPath, "router and manifest must come from the same Agent Utilities plugin source");

const manifest = JSON.parse(fs.readFileSync(resolvedManifestPath, "utf8"));
assert.equal(manifest.name, "agent-utilities");
assert.equal(manifest.skills, "./skills/");
assert.ok(
  fs.statSync(path.resolve(path.dirname(resolvedManifestPath), "..", "skills", "model-routing", "SKILL.md")).isFile(),
  "Agent Utilities manifest skill root must expose model-routing",
);

function releaseTuple(version) {
  const match = /^(\d+)\.(\d+)\.(\d+)$/.exec(version);
  assert.ok(match, `expected a stable semantic release version, got ${version}`);
  return match.slice(1).map(Number);
}

function releaseAtLeast(actual, minimum) {
  const left = releaseTuple(actual);
  const right = releaseTuple(minimum);
  for (let index = 0; index < left.length; index += 1) {
    if (left[index] !== right[index]) return left[index] > right[index];
  }
  return true;
}

assert.ok(
  releaseAtLeast(manifest.version, MINIMUM_AGENT_UTILITIES_VERSION),
  `Agent Utilities ${MINIMUM_AGENT_UTILITIES_VERSION} or newer is required; got ${manifest.version}`,
);

const router = await import(pathToFileURL(resolvedRouterPath));
const {
  CONTRACT_VERSION,
  createEmptyState,
  handleRequest,
  stableDigest,
  validateCatalog,
} = router;

assert.equal(CONTRACT_VERSION, "agent-utilities/model-routing/v1");

const NOW = Date.parse("2026-08-04T12:00:00.000Z");
const INPUT_DIGEST = "a".repeat(64);
const INSTRUCTION_DIGEST = "b".repeat(64);
const R52 = {
  schema: "agent-utilities/r52-readiness/v1",
  hostReadiness: { state: "ready", evidenceDigest: "c".repeat(64) },
  taskReadiness: { state: "ready", evidenceDigest: "d".repeat(64) },
  transportReadiness: { state: "ready", evidenceDigest: "e".repeat(64) },
  executionHost: { identityDigest: "f".repeat(64), platform: "darwin" },
  targetPlatform: { identityDigest: "1".repeat(64), platform: "linux" },
};

function request(command, fields = {}) {
  return {
    contractVersion: CONTRACT_VERSION,
    command,
    callerKind: "machine-utilities",
    role: "implementation",
    workShape: {
      ambiguity: "low",
      novelty: "low",
      repetition: "high",
      decomposability: "high",
      unitVolume: "medium",
      semanticRisk: "low",
      verificationStrength: "high",
    },
    ...fields,
  };
}

function handled(input, options = {}) {
  const result = handleRequest(input, { now: NOW, ...options });
  assert.equal(result.response.ok, true, JSON.stringify(result.response));
  return result.response;
}

function configuredPolicy(seed) {
  const provider = "fixture_provider";
  const model = "fixture_model";
  const policy = {
    schemaVersion: 1,
    providers: {
      [provider]: {
        carrierId: seed.selected.carrierId,
        executionSurface: seed.selected.executionSurface,
        account: "fixture_account",
        locality: "external",
        retention: "provider_default",
      },
    },
    models: {
      [model]: {
        provider,
        carrierId: seed.selected.carrierId,
        requestedModel: seed.selected.model,
        efforts: [seed.selected.effort],
        roles: ["implementation"],
        relativeCostIndex: 1,
      },
    },
    roles: { implementation: { tiers: [[model]] } },
  };
  const validation = validateCatalog(policy);
  assert.equal(validation.ok, true, JSON.stringify(validation));
  return { policy, policyDigest: validation.policy.digest };
}

function trustedTaskAuthorityAttestor() {
  return ({ authority }) => {
    const controller = { threadId: "fixture_controller", permissionProfile: "disabled", originator: "user" };
    const facts = { ...authority, controller };
    return {
      attestorId: "agent-utilities-task-authority-attestor-v1",
      attestationDigest: stableDigest({ facts, source: "machine-utilities-user-turn-attestor" }),
      attestedAt: new Date(NOW).toISOString(),
      authorityFactsDigest: stableDigest(facts),
      controller,
    };
  };
}

test("Machine Utilities task create and message wires match the shared router", () => {
  const seed = handled(request("resolve", {
    adapterId: "codex-task-create",
    dispatchKind: "task_create",
    budgetEffect: "start",
    r52: R52,
  })).decision;

  const noConfigStatus = handleRequest(request("resolve", {
    adapterId: "codex-task-message",
    dispatchKind: "task_message",
    budgetEffect: "none",
    actionId: "status-action",
    r52: R52,
  }));
  assert.equal(noConfigStatus.response.reason, "prior_route_unknown");

  const noConfigAdjustment = handleRequest(request("admit", {
    adapterId: "codex-task-message",
    dispatchKind: "task_message",
    budgetEffect: "adjust_active",
    requestId: "default-adjustment",
    actionId: "default-adjustment",
    r52: R52,
  }));
  assert.equal(noConfigAdjustment.response.reason, "prior_route_unknown");

  const { policy, policyDigest } = configuredPolicy(seed);
  const state = createEmptyState();
  const scopes = { task: "fixture_task", run: "fixture_run", project: "fixture_project" };
  const authority = {
    authorityId: "fixture_authority",
    objectiveEpoch: "fixture_epoch",
    objectiveDigest: INPUT_DIGEST,
    senderOwner: "machine_utilities",
    accountScope: "fixture_account",
    carrierId: seed.selected.carrierId,
    adapterId: "codex-task-create",
    policyDigest,
    destinationScope: "fixture_host",
    destinationClass: "visible_task",
    maxTaskCount: 1,
    currentTurn: "fixture_turn",
    expiresAt: "2026-08-05T12:00:00.000Z",
    explicitUserInstructionDigest: INSTRUCTION_DIGEST,
  };
  const minted = handled(request("mint-task-authority", { authority }), {
    catalog: policy,
    state,
    trustedTaskAuthorityAttestor: trustedTaskAuthorityAttestor(),
  });
  assert.equal(minted.reason, "task_authority_minted");
  const admission = handled(request("admit", {
    adapterId: "codex-task-create",
    dispatchKind: "task_create",
    budgetEffect: "start",
    requestId: "fixture_create",
    frozenInputDigest: INPUT_DIGEST,
    forecast: { activeAgentMinutes: "1" },
    scopes,
    taskAuthorityId: minted.authority.authorityId,
    objectiveEpoch: minted.authority.objectiveEpoch,
    objectiveDigest: minted.authority.objectiveDigest,
    instructionDigest: authority.explicitUserInstructionDigest,
    senderOwner: minted.authority.senderOwner,
    accountScope: minted.authority.accountScope,
    destinationScope: "fixture_host",
    destinationClass: "visible_task",
    currentTurn: "fixture_turn",
    r52: R52,
  }), { catalog: policy, state });
  assert.equal(admission.reservation.binding.r52.digest, stableDigest(R52));
  const claimed = handled(request("claim-dispatch", {
    reservationId: admission.reservation.reservationId,
    frozenInputDigest: INPUT_DIGEST,
    dispatchIdentity: {
      hostScope: "fixture_host",
      accountScope: "fixture_account",
      dispatchKind: "task_create",
      sessionId: "fixture_session",
      toolId: "codex-task",
      toolVersion: "v1",
    },
    taskAuthorityId: minted.authority.authorityId,
  }), { catalog: policy, state });
  const priorRoute = {
    reservationId: admission.reservation.reservationId,
    claimId: claimed.claimId,
    carrierId: admission.reservation.selected.carrierId,
    model: admission.reservation.selected.model,
    effort: admission.reservation.selected.effort,
    adapterId: admission.reservation.binding.adapterId,
    adapterVersion: admission.reservation.binding.adapterVersion,
    policyDigest: admission.reservation.policyDigest,
    hostScope: claimed.reservation.claimed.hostScope,
    accountScope: claimed.reservation.claimed.accountScope,
    sessionId: claimed.reservation.claimed.sessionId,
    toolId: claimed.reservation.claimed.toolId,
    toolVersion: claimed.reservation.claimed.toolVersion,
    workClassDigest: admission.reservation.workClassDigest,
    r52Digest: admission.reservation.binding.r52.digest,
  };
  const messageIdentity = {
    hostScope: priorRoute.hostScope,
    accountScope: priorRoute.accountScope,
    dispatchKind: "task_message",
    sessionId: priorRoute.sessionId,
    toolId: priorRoute.toolId,
    toolVersion: priorRoute.toolVersion,
  };

  const status = handled(request("resolve", {
    adapterId: "codex-task-message",
    dispatchKind: "task_message",
    budgetEffect: "none",
    actionId: "configured_status",
    priorRoute,
    priorWorkClassDigest: priorRoute.workClassDigest,
    r52: R52,
    dispatchIdentity: messageIdentity,
  }), { catalog: policy, state });
  assert.equal(status.decision.actionReceipt.schema, "agent-utilities/action-receipt/v1");
  assert.equal(status.decision.actionReceipt.workClassDigest, priorRoute.workClassDigest);
  assert.equal(status.decision.actionReceipt.priorRouteDigest, stableDigest(priorRoute));

  const adjustment = handled(request("admit", {
    adapterId: "codex-task-message",
    dispatchKind: "task_message",
    budgetEffect: "adjust_active",
    requestId: "configured_adjustment",
    frozenInputDigest: INPUT_DIGEST,
    forecast: { activeAgentMinutes: "1" },
    scopes,
    activeReservationId: priorRoute.reservationId,
    priorRoute,
    priorWorkClassDigest: priorRoute.workClassDigest,
    r52: R52,
    dispatchIdentity: messageIdentity,
  }), { catalog: policy, state });
  assert.equal(adjustment.reason, "active_budget_adjusted");
  assert.equal(adjustment.reservation.claimId, priorRoute.claimId);
});
