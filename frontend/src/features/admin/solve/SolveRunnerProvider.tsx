import { useCallback, useMemo, useState, type ReactNode } from "react";
import { runSolve } from "./runSolve";
import { SolveRunnerContext, type SolveRun, type StartSolveInput } from "./solveRunnerContext";

export function SolveRunnerProvider({ children }: { children: ReactNode }) {
  const [runs, setRuns] = useState<Record<string, SolveRun>>({});

  const startSolve = useCallback((input: StartSolveInput) => {
    setRuns((current) => {
      if (current[input.recordId]?.phase === "running") return current;

      return {
        ...current,
        [input.recordId]: {
          recordId: input.recordId,
          userName: input.userName,
          phase: "running",
          listings: {},
          labels: input.labels,
          kick: input.kick ? "pending" : "not-requested",
          errors: [],
        },
      };
    });

    void runSolve(input, (change) =>
      setRuns((current) => {
        const run = current[input.recordId];
        return run ? { ...current, [input.recordId]: change(run) } : current;
      }),
    );
  }, []);

  const clearRun = useCallback((recordId: string) => {
    setRuns((current) => {
      if (current[recordId]?.phase === "running") return current;
      const next = { ...current };
      delete next[recordId];
      return next;
    });
  }, []);

  const value = useMemo(() => ({ runs, startSolve, clearRun }), [runs, startSolve, clearRun]);

  return <SolveRunnerContext.Provider value={value}>{children}</SolveRunnerContext.Provider>;
}
