import { useRef, useState } from "react";
import { decideAsFinder } from "../api/matchDecisions";
import { matchingError } from "../api/matches";

type Decision = "confirm" | "reject";

type Props = {
  matchId: string;
  onDecisionSaved: () => void;
  onRefresh: () => void;
};

export function FinderDecisionPanel({
  matchId,
  onDecisionSaved,
  onRefresh,
}: Props) {
  const [selected, setSelected] =
    useState<Decision | null>(null);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const submitting = useRef(false);

  async function submitDecision() {
    if (!selected || submitting.current) {
      return;
    }

    submitting.current = true;
    setSaving(true);
    setError(null);

    try {
      await decideAsFinder(matchId, selected);
      onDecisionSaved();
    } catch (reason: unknown) {
      setError(matchingError(reason));
    } finally {
      submitting.current = false;
      setSaving(false);
    }
  }

  return (
    <section
      aria-label="Your match decision"
      className="rounded-2xl border border-amber-200 bg-amber-50 p-5"
    >
      <h2 className="text-lg font-semibold text-amber-950">
        Does this match the item you found?
      </h2>

      <p className="mt-2 text-sm leading-6 text-amber-900">
        The lost reporter confirmed this claim. Review their lost
        report before deciding.
      </p>

      {error && (
        <div
          role="alert"
          className="mt-4 rounded-xl border border-rose-200 bg-white p-4"
        >
          <p className="text-sm text-rose-800">{error}</p>

          <button
            type="button"
            disabled={saving}
            onClick={onRefresh}
            className="mt-2 text-sm font-semibold text-amber-800 underline disabled:opacity-50"
          >
            Refresh match
          </button>
        </div>
      )}

      {selected === null ? (
        <div className="mt-4 flex flex-wrap gap-3">
          <button
            type="button"
            onClick={() => {
              setError(null);
              setSelected("confirm");
            }}
            className="rounded-lg bg-amber-700 px-4 py-2.5 text-sm font-semibold text-white hover:bg-amber-800"
          >
            Confirm match
          </button>

          <button
            type="button"
            onClick={() => {
              setError(null);
              setSelected("reject");
            }}
            className="rounded-lg border border-rose-300 bg-white px-4 py-2.5 text-sm font-semibold text-rose-700 hover:bg-rose-50"
          >
            Reject match
          </button>
        </div>
      ) : (
        <div className="mt-4 rounded-xl border border-amber-200 bg-white p-4">
          <h3 className="font-semibold text-slate-900">
            {selected === "confirm"
              ? "Confirm this match?"
              : "Reject this match?"}
          </h3>

          <p className="mt-2 text-sm leading-6 text-slate-600">
            {selected === "confirm"
              ? "Both parties will have confirmed. You can then view the lost reporter's saved contact details."
              : "This match will be closed. You cannot change this decision here."}
          </p>

          <div className="mt-4 flex flex-wrap gap-3">
            <button
              type="button"
              disabled={saving}
              onClick={() => void submitDecision()}
              className={`rounded-lg px-4 py-2.5 text-sm font-semibold text-white disabled:cursor-wait disabled:opacity-60 ${
                selected === "confirm"
                  ? "bg-amber-700 hover:bg-amber-800"
                  : "bg-rose-700 hover:bg-rose-800"
              }`}
            >
              {saving
                ? "Saving..."
                : selected === "confirm"
                  ? "Yes, confirm match"
                  : "Yes, reject match"}
            </button>

            <button
              type="button"
              disabled={saving}
              onClick={() => {
                setSelected(null);
                setError(null);
              }}
              className="rounded-lg border border-slate-300 px-4 py-2.5 text-sm font-semibold text-slate-700 hover:bg-slate-50 disabled:opacity-50"
            >
              Go back
            </button>
          </div>
        </div>
      )}
    </section>
  );
}