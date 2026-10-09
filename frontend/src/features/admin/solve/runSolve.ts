import { kickUser } from "../api/admin";
import {
  deleteAdminItem,
  finishSolve,
  saveKickResult,
  saveListingResult,
  startSolve,
  type SolveResult,
  type SpamRecordListing,
} from "../api/spamRecords";
import type { ListingRunState, SolveRun, StartSolveInput } from "./solveRunnerContext";

const MAX_ATTEMPTS = 5;
const RETRY_DELAY_MS = 1000;

type Update = (change: (run: SolveRun) => SolveRun) => void;

const wait = (ms: number) => new Promise((resolve) => window.setTimeout(resolve, ms));

function errorOf(reason: unknown) {
  const response = reason as { status?: number; body?: { error?: string } };
  return { status: response?.status, message: response?.body?.error ?? "" };
}

async function deleteWithRetries(listing: SpamRecordListing): Promise<SolveResult> {
  for (let attempt = 1; attempt <= MAX_ATTEMPTS; attempt++) {
    try {
      await deleteAdminItem(listing.listingType, listing.listingId);
      return "DELETED";
    } catch (reason) {
      const { status } = errorOf(reason);
      if (status === 404 || status === 409) return "SKIPPED";
    }

    if (attempt < MAX_ATTEMPTS) await wait(RETRY_DELAY_MS);
  }

  return "FAILED";
}

async function kickOnce(userId: string): Promise<"KICKED" | "FAILED"> {
  try {
    await kickUser(userId);
    return "KICKED";
  } catch (reason) {
    const { status, message } = errorOf(reason);
    return status === 400 && message.toLowerCase().includes("already kicked") ? "KICKED" : "FAILED";
  }
}

const runState: Record<SolveResult, ListingRunState> = {
  DELETED: "deleted",
  SKIPPED: "skipped",
  FAILED: "failed",
};

export async function runSolve(input: StartSolveInput, update: Update) {
  const setListing = (listingId: string, state: ListingRunState) =>
    update((run) => ({ ...run, listings: { ...run.listings, [listingId]: state } }));
  const addError = (message: string) => update((run) => ({ ...run, errors: [...run.errors, message] }));
  const label = (listingId: string) => input.labels[listingId] ?? `Listing ${listingId.slice(0, 8)}`;

  let detail;

  try {
    detail = await startSolve(input.recordId, input.kick, input.resume);
  } catch (reason) {
    addError(errorOf(reason).message || "Couldn't start Solve. Please try again.");
    update((run) => ({ ...run, phase: "stopped" }));
    return;
  }

  update((run) => ({
    ...run,
    kick: detail.kickStatus === "PENDING" ? "pending" : detail.kickStatus === "KICKED" ? "kicked" : detail.kickStatus === "FAILED" ? "failed" : "not-requested",
    listings: Object.fromEntries(
      detail.listings.map((listing) => [
        listing.listingId,
        listing.solveResult === "DELETED" ? "deleted" : listing.solveResult === "SKIPPED" ? "skipped" : "pending",
      ]),
    ),
  }));

  let allDone = true;
  const remaining = detail.listings.filter(
    (listing) => listing.solveResult !== "DELETED" && listing.solveResult !== "SKIPPED",
  );

  for (const listing of remaining) {
    setListing(listing.listingId, "deleting");
    const result = await deleteWithRetries(listing);
    setListing(listing.listingId, runState[result]);

    if (result === "FAILED") {
      allDone = false;
      addError(`Couldn't delete "${label(listing.listingId)}" after ${MAX_ATTEMPTS} tries.`);
    }

    try {
      await saveListingResult(input.recordId, listing.listingId, result);
    } catch {
      allDone = false;
      addError(`Couldn't save the result for "${label(listing.listingId)}". Solve again to continue.`);
    }
  }

  if (detail.kickStatus === "PENDING") {
    update((run) => ({ ...run, kick: "kicking" }));
    const kick = await kickOnce(input.userId);
    update((run) => ({ ...run, kick: kick === "KICKED" ? "kicked" : "failed" }));

    if (kick === "FAILED") addError(`Couldn't kick ${input.userName}. Kick them from User Management.`);

    try {
      await saveKickResult(input.recordId, kick);
    } catch {
      allDone = false;
      addError("Couldn't save the kick result. Solve again to continue.");
    }
  }

  if (!allDone) {
    update((run) => ({ ...run, phase: "stopped" }));
    return;
  }

  try {
    await finishSolve(input.recordId);
    update((run) => ({ ...run, phase: "solved" }));
  } catch (reason) {
    addError(errorOf(reason).message || "Couldn't finish Solve. Please try again.");
    update((run) => ({ ...run, phase: "stopped" }));
  }
}
