import { createContext, useContext } from "react";

export type ListingRunState = "pending" | "deleting" | "deleted" | "skipped" | "failed";

export type KickRunState = "not-requested" | "pending" | "kicking" | "kicked" | "failed";

export type SolveRun = {
  recordId: string;
  userName: string;
  phase: "running" | "solved" | "stopped";
  listings: Record<string, ListingRunState>;
  labels: Record<string, string>;
  kick: KickRunState;
  errors: string[];
};

export type StartSolveInput = {
  recordId: string;
  userId: string;
  userName: string;
  kick: boolean;
  resume: boolean;
  labels: Record<string, string>;
};

export type SolveRunnerValue = {
  runs: Record<string, SolveRun>;
  startSolve: (input: StartSolveInput) => void;
  clearRun: (recordId: string) => void;
};

export const SolveRunnerContext = createContext<SolveRunnerValue | null>(null);

export function useSolveRunner(): SolveRunnerValue {
  const value = useContext(SolveRunnerContext);
  if (!value) throw new Error("useSolveRunner must be used inside SolveRunnerProvider.");
  return value;
}
