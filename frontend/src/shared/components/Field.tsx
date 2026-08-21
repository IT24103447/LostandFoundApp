import { cloneElement, isValidElement, type ReactNode } from "react";
import type { ReactElement } from "react";

type FieldProps = {
  id: string;
  label: string;
  error?: string;
  hint?: string;
  children: ReactNode;
};

export function Field({ id, label, error, hint, children }: FieldProps) {
  const errorId = `${id}-error`;
  const hintId = `${id}-hint`;
  const describedBy =
    [hint && hintId, error && errorId].filter(Boolean).join(" ") || undefined;

  // Inject aria-describedby + aria-invalid onto the single child input so screen
  // readers read both hint and error. Industry-standard pattern: clone the child
  // and merge accessibility attrs onto its existing props (later keys win in cloneElement).
  let enhancedChild: ReactNode = children;
  if (isValidElement(children)) {
    const child = children as ReactElement<Record<string, unknown>>;
    if (child.props.id === id) {
      enhancedChild = cloneElement(child, {
        ...child.props,
        "aria-describedby": describedBy,
        "aria-invalid": !!error || undefined,
      });
    }
  }

  return (
    <div className="space-y-1">
      <label htmlFor={id} className="block text-sm font-medium text-gray-700">
        {label}
  </label>
      {enhancedChild}
      {hint && (
        <p id={hintId} className="text-xs text-gray-500">
          {hint}
    </p>
      )}
      {error && (
        <p id={errorId} className="text-xs text-red-600" role="alert">
          {error}
    </p>
      )}
</div>
  );
}
