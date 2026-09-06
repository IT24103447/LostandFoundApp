import type { ReactNode } from "react";

type WizardFieldProps = {
  id: string;
  label: string;
  required?: boolean;
  hint?: string;
  error?: string;
  trailing?: ReactNode;
  children: ReactNode;
};

export function WizardField({ id, label, required, hint, error, trailing, children }: WizardFieldProps) {
  return (
    <div>
      <div className="mb-2 flex items-baseline justify-between">
        <label htmlFor={id} className="text-sm font-semibold text-gray-900">
          {label}
          {required && <span className="ml-0.5 text-red-500">*</span>}
        </label>
        {trailing}
      </div>
      {children}
      {error ? (
        <p className="mt-1.5 text-sm text-red-500">{error}</p>
      ) : hint ? (
        <p className="mt-1.5 text-sm text-gray-500">{hint}</p>
      ) : null}
    </div>
  );
}

export function wizardInputClass(hasError?: boolean, extra = ""): string {
  return [
    "w-full rounded-xl border bg-white px-4 py-3 text-[15px] text-gray-900 placeholder:text-gray-400 shadow-sm transition-colors",
    "focus:outline-none focus:ring-2 focus:ring-indigo-500/30",
    hasError ? "border-red-400 focus:border-red-400" : "border-gray-200 focus:border-indigo-500",
    extra,
  ].join(" ");
}
