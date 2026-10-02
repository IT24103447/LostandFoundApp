import { useCallback, useEffect, useRef, useState } from "react";
import { getAppealEditWarning } from "../api/appeals";
import {
  AppealEditWarningModal,
  type AppealEditWarningStage,
} from "../components/AppealEditWarningModal";

export function useAppealEditWarning(
  type: "lost" | "found",
  id: string | undefined,
  onLeave: () => void,
) {
  const [warn, setWarn] = useState(false);
  const [stage, setStage] = useState<AppealEditWarningStage | null>(null);
  const resolverRef = useRef<((confirmed: boolean) => void) | null>(null);

  useEffect(() => {
    if (!id) return;

    const controller = new AbortController();

    getAppealEditWarning(type, id, controller.signal)
      .then((result) => {
        if (controller.signal.aborted || !result.warn) return;
        setWarn(true);
        setStage("open");
      })
      .catch(() => {
        // The warning is advisory, so a failed check lets the edit go ahead as before.
      });

    return () => controller.abort();
  }, [type, id]);

  const confirmEdit = useCallback((): Promise<boolean> => {
    if (!warn) {
      return Promise.resolve(true);
    }

    setStage("save");

    return new Promise<boolean>((resolve) => {
      resolverRef.current = resolve;
    });
  }, [warn]);

  const answer = (confirmed: boolean) => {
    setStage(null);

    const resolve = resolverRef.current;
    resolverRef.current = null;

    if (resolve) {
      resolve(confirmed);
    } else if (!confirmed) {
      onLeave();
    }
  };

  const warningModal = stage ? (
    <AppealEditWarningModal
      stage={stage}
      onConfirm={() => answer(true)}
      onCancel={() => answer(false)}
    />
  ) : null;

  return { confirmEdit, warningModal };
}
