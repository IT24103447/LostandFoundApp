import { useRef } from "react";
import { Calendar } from "lucide-react";
import { wizardInputClass } from "./WizardField";

type FoundDateFieldProps = {
  id: string;
  value: string;
  onChange: (value: string) => void;
  onBlur?: () => void;
  hasError?: boolean;
  max?: string;
};

function formatDisplay(value: string): string {
  if (!value) return "";

  const d = new Date(`${value}T00:00:00`);

  if (Number.isNaN(d.getTime())) return "";

  return d.toLocaleDateString(undefined, {
    year: "numeric",
    month: "long",
    day: "numeric",
  });
}

/**
 * Found-item-only copy of DateField, so the Lost Item flow's DateField.tsx
 * never needs to change. Only the empty-state copy differs ("the date you
 * found the item" / aria-label "Date found" vs. "…lost the item" / "Date
 * lost") — everything else is identical, intentionally duplicated rather
 * than parameterized.
 */
export function FoundDateField({ id, value, onChange, onBlur, hasError, max }: FoundDateFieldProps) {
  const nativeRef = useRef<HTMLInputElement>(null);

  const openPicker = () => {
    const input = nativeRef.current;

    if (!input) return;

    if ("showPicker" in input && typeof input.showPicker === "function") {
      input.showPicker();
    }
  };

  return (
    <div className="relative">
      {/* Visible custom field */}
      <div className={wizardInputClass(hasError, "flex items-center gap-2.5 text-left")}>
        <Calendar className="h-[18px] w-[18px] shrink-0 text-gray-500" />

        <span className={value ? "text-gray-900" : "text-gray-400"}>
          {value ? formatDisplay(value) : "Select the date you found the item"}
        </span>
      </div>

      {/* Invisible native date input covering the ENTIRE field */}
      <input
        ref={nativeRef}
        id={id}
        type="date"
        value={value}
        max={max}
        onChange={(e) => onChange(e.target.value)}
        onBlur={onBlur}
        onClick={openPicker}
        className="absolute inset-0 h-full w-full cursor-pointer opacity-0"
        aria-label="Date found"
      />
    </div>
  );
}
