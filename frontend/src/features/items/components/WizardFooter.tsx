import { ArrowLeft, ArrowRight } from "lucide-react";
import type { WizardStep } from "./StepIndicator";

type WizardFooterProps = {
  step: WizardStep;
  isSubmitting?: boolean;
  onCancel: () => void;
  onBack: () => void;
  onContinue: () => void;
};

/**
 * The prototype places the step footer INSIDE the card, separated by a hairline
 * divider: "Step X of 3" on the left, actions on the right.
 */
export function WizardFooter({ step, isSubmitting, onCancel, onBack, onContinue }: WizardFooterProps) {
  return (
    <div className="mt-8 border-t border-gray-100 pt-6">
      <div className="flex items-center justify-between gap-4">
        <span className="text-[15px] text-gray-500">Step {step} of 3</span>

        <div className="flex items-center gap-3">
          {step === 1 ? (
            <button
              type="button"
              onClick={onCancel}
              className="rounded-xl border border-gray-200 bg-white px-6 py-3 text-[15px] font-semibold text-gray-800 transition-colors hover:bg-gray-50"
            >
              Cancel
            </button>
          ) : (
            <button
              type="button"
              onClick={onBack}
              className="flex items-center gap-2 rounded-xl border border-gray-200 bg-white px-6 py-3 text-[15px] font-semibold text-gray-800 transition-colors hover:bg-gray-50"
            >
              <ArrowLeft className="h-4 w-4" />
              Back
            </button>
          )}

          {step < 3 ? (
            <button
              type="button"
              onClick={onContinue}
              className="flex items-center gap-2 rounded-xl bg-indigo-600 px-6 py-3 text-[15px] font-semibold text-white transition-colors hover:bg-indigo-700"
            >
              Continue
              <ArrowRight className="h-4 w-4" />
            </button>
          ) : (
            <button
              type="submit"
              disabled={isSubmitting}
              className="rounded-xl bg-indigo-600 px-6 py-3 text-[15px] font-semibold text-white transition-colors hover:bg-indigo-700 disabled:opacity-60"
            >
              {isSubmitting ? "Submitting…" : "Report Lost Item"}
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
