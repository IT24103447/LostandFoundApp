import { useCallback, useState } from "react";
import {
  getFinderReturnContact,
  getLostReporterReturnContact,
} from "../api/matchDecisions";
import type { ReportType } from "../api/matches";
import { useMatchRequest } from "../hooks/useMatchRequest";

type Props = {
  matchId: string;
  yourRole: ReportType;
};

type ContactFieldProps = {
  label: string;
  value: string;
};

function ContactField({ label, value }: ContactFieldProps) {
  const [feedback, setFeedback] = useState<string | null>(null);

  async function copyValue() {
    try {
      await navigator.clipboard.writeText(value);
      setFeedback("Copied");
    } catch {
      setFeedback("Select the text and copy it manually.");
    }
  }

  return (
    <div className="rounded-xl border border-emerald-200 bg-white p-4">
      <p className="text-xs font-semibold uppercase tracking-wide text-slate-500">
        {label}
      </p>

      <div className="mt-2 flex flex-wrap items-center justify-between gap-3">
        <p className="min-w-0 select-text break-all text-sm font-medium text-slate-900">
          {value}
        </p>

        <button
          type="button"
          onClick={() => void copyValue()}
          aria-label={`Copy ${label.toLowerCase()}`}
          className="rounded-lg border border-emerald-200 px-3 py-2 text-sm font-semibold text-emerald-800 hover:bg-emerald-50"
        >
          Copy
        </button>
      </div>

      {feedback && (
        <p role="status" className="mt-2 text-xs text-emerald-800">
          {feedback}
        </p>
      )}
    </div>
  );
}

export function ArrangeReturnPanel({
  matchId,
  yourRole,
}: Props) {
  const load = useCallback(
    (signal: AbortSignal) =>
      yourRole === "LOST"
        ? getFinderReturnContact(matchId, signal)
        : getLostReporterReturnContact(matchId, signal),
    [matchId, yourRole],
  );

  const { state, retry } = useMatchRequest(load);

  const otherParty =
    yourRole === "LOST" ? "finder" : "lost reporter";

  return (
    <section
      aria-label="Arrange the return"
      className="rounded-2xl border border-emerald-200 bg-emerald-50 p-5"
    >
      <h2 className="text-lg font-semibold text-emerald-950">
        Arrange the return
      </h2>

      <p className="mt-2 text-sm text-emerald-900">
        Contact the {otherParty} to arrange returning the item.
      </p>

      {state.status === "loading" && (
        <p role="status" className="mt-4 text-sm text-slate-600">
          Loading contact details...
        </p>
      )}

      {state.status === "error" && (
        <div
          role="alert"
          className="mt-4 rounded-xl border border-emerald-200 bg-white p-4"
        >
          <p className="text-sm text-slate-700">
            {state.error}
          </p>

          <button
            type="button"
            onClick={retry}
            className="mt-3 rounded-lg border border-emerald-300 px-3 py-2 text-sm font-semibold text-emerald-800 hover:bg-emerald-50"
          >
            Try again
          </button>
        </div>
      )}

      {state.status === "success" && (
        <div className="mt-4 grid gap-3 sm:grid-cols-2">
          <ContactField
            label="Email"
            value={state.data.email}
          />
          <ContactField
            label="Phone"
            value={state.data.phone}
          />
        </div>
      )}
    </section>
  );
}