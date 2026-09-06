import { useState, useRef, useEffect } from "react";
import { ChevronDown } from "lucide-react";
import { LOST_ITEM_CATEGORIES } from "../schemas/reportLostItemSchema";
import { wizardInputClass } from "./WizardField";

type CategorySelectProps = {
  id: string;
  value: string;
  onChange: (value: string) => void;
  onBlur?: () => void;
  hasError?: boolean;
};

export function CategorySelect({ id, value, onChange, onBlur, hasError }: CategorySelectProps) {
  const [open, setOpen] = useState(false);
  const ref = useRef<HTMLDivElement>(null);

  const selected = LOST_ITEM_CATEGORIES.find((c) => c.value === value);
  const SelectedIcon = selected?.icon;

  useEffect(() => {
    function onClickOutside(e: MouseEvent) {
      if (ref.current && !ref.current.contains(e.target as Node)) {
        setOpen(false);
        onBlur?.();
      }
    }
    document.addEventListener("mousedown", onClickOutside);
    return () => document.removeEventListener("mousedown", onClickOutside);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="relative" ref={ref}>
      <button
        id={id}
        type="button"
        onClick={() => setOpen((v) => !v)}
        className={wizardInputClass(hasError, "flex items-center justify-between text-left")}
      >
        <span className="flex items-center gap-2.5">
          {SelectedIcon ? (
            <SelectedIcon className="h-[18px] w-[18px] text-gray-500" />
          ) : (
            <span className="h-[18px] w-[18px]" />
          )}
          <span className={selected ? "text-gray-900" : "text-gray-400"}>
            {selected?.label ?? "Select a category"}
          </span>
        </span>
        <ChevronDown className={`h-4 w-4 text-gray-400 transition-transform ${open ? "rotate-180" : ""}`} />
      </button>

      {open && (
        <div className="absolute z-20 mt-1.5 w-full overflow-hidden rounded-xl border border-gray-200 bg-white py-1.5 shadow-xl animate-fade-in">
          {LOST_ITEM_CATEGORIES.map((cat) => {
            const Icon = cat.icon;
            const isSelected = cat.value === value;
            return (
              <button
                key={cat.value}
                type="button"
                onClick={() => {
                  onChange(cat.value);
                  setOpen(false);
                  onBlur?.();
                }}
                className={`flex w-full items-center gap-3 px-4 py-2.5 text-left text-[15px] transition-colors ${
                  isSelected ? "bg-indigo-50 text-indigo-700" : "text-gray-700 hover:bg-gray-50"
                }`}
              >
                <Icon className="h-[18px] w-[18px] text-gray-500" />
                {cat.label}
              </button>
            );
          })}
        </div>
      )}
    </div>
  );
}
