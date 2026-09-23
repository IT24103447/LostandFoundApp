import { useCallback, useEffect, useState } from "react";
import { matchingError } from "../api/matches";

type RequestState<T> =
  | { status: "loading"; data: null; error: null }
  | { status: "success"; data: T; error: null }
  | { status: "error"; data: null; error: string };

export function useMatchRequest<T>(
  load: (signal: AbortSignal) => Promise<T>,
) {
  const [state, setState] = useState<RequestState<T>>({
    status: "loading",
    data: null,
    error: null,
  });

  const [revision, setRevision] = useState(0);

  const retry = useCallback(() => {
    setRevision((value) => value + 1);
  }, []);

  useEffect(() => {
    const controller = new AbortController();

    setState({
      status: "loading",
      data: null,
      error: null,
    });

    load(controller.signal)
      .then((data) => {
        if (!controller.signal.aborted) {
          setState({
            status: "success",
            data,
            error: null,
          });
        }
      })
      .catch((reason: unknown) => {
        if (!controller.signal.aborted) {
          setState({
            status: "error",
            data: null,
            error: matchingError(reason),
          });
        }
      });

    return () => controller.abort();
  }, [load, revision]);

  return { state, retry };
}