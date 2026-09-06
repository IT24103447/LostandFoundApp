import { useRef } from "react";
import { Calendar } from "lucide-react";
import { wizardInputClass } from "./WizardField";

type DateFieldProps = {
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

export function DateField({
  id,
  value,
  onChange,
  onBlur,
  hasError,
  max,
}: DateFieldProps) {
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
      <div
        className={wizardInputClass(
          hasError,
          "flex items-center gap-2.5 text-left"
        )}
      >
        <Calendar className="h-[18px] w-[18px] shrink-0 text-gray-500" />

        <span className={value ? "text-gray-900" : "text-gray-400"}>
          {value
            ? formatDisplay(value)
            : "Select the date you lost the item"}
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
        aria-label="Date lost"
      />
    </div>
  );
}