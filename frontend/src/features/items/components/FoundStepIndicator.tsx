import { Check, ArrowRight } from "lucide-react";
import type { WizardStep } from "./StepIndicator";

const LABELS: [string, string, string] = ["Found Item Details", "When & Where", "Verification"];

/**
 * Found-item-only copy of StepIndicator, so the Lost Item flow's
 * StepIndicator.tsx never needs to change. Only the step labels differ
 * ("Found Item Details" vs. "Item Details") — everything else is identical
 * rendering logic, intentionally duplicated rather than parameterized.
 */
export function FoundStepIndicator({ current }: { current: WizardStep }) {
  const steps: { step: WizardStep; label: string }[] = [1, 2, 3].map((step) => ({
    step: step as WizardStep,
    label: LABELS[step - 1],
  }));

  return (
    <div className="flex items-center">
      <span className="mr-6 shrink-0 text-sm text-gray-400">Step {current} of 3</span>
      <div className="flex items-center">
        {steps.map(({ step, label }, i) => {
          const isDone = step < current;
          const isActive = step === current;
          return (
            <div key={step} className="flex items-center">
              <div className="flex items-center gap-2.5">
                <span
                  className={`flex h-8 w-8 shrink-0 items-center justify-center rounded-full text-sm font-semibold transition-colors ${
                    isDone
                      ? "bg-indigo-600 text-white"
                      : isActive
                        ? "bg-indigo-600 text-white"
                        : "bg-gray-100 text-gray-400"
                  }`}
                >
                  {isDone ? <Check className="h-4 w-4" strokeWidth={3} /> : String(step).padStart(2, "0")}
                </span>
                <span
                  className={`text-sm font-medium whitespace-nowrap ${
                    isActive ? "text-gray-900" : isDone ? "text-gray-500" : "text-gray-400"
                  }`}
                >
                  {label}
                </span>
              </div>
              {i < steps.length - 1 && (
                <ArrowRight
                  className={`mx-4 h-4 w-4 shrink-0 ${step < current ? "text-indigo-300" : "text-gray-300"}`}
                />
              )}
            </div>
          );
        })}
      </div>
    </div>
  );
}
