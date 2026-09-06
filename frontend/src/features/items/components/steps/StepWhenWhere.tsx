import { useFormContext, Controller } from "react-hook-form";
import { MapPin, Building2, LayoutGrid, Navigation } from "lucide-react";
import type { ReportLostItemFormValues } from "../../schemas/reportLostItemSchema";
import { WizardField, wizardInputClass } from "../WizardField";
import { DateField } from "../DateField";
import { MapIllustration } from "../Illustrations";

const TIPS = [
  { icon: Building2, text: "Use a recognizable building or area" },
  { icon: LayoutGrid, text: "Include a room, floor, or section when useful" },
  { icon: Navigation, text: "Example: Library, 2nd Floor" },
];

export function StepWhenWhere() {
  const {
    register,
    control,
    watch,
    formState: { errors },
  } = useFormContext<ReportLostItemFormValues>();

  const location = watch("lastKnownLocation") ?? "";
  const todayStr = new Date().toISOString().split("T")[0];

  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[1fr_320px]">
      <div className="rounded-2xl border border-gray-100 bg-white p-8 shadow-sm">
        <h2 className="text-2xl font-bold text-gray-900">Lost details</h2>
        <p className="mt-1.5 text-[15px] text-gray-500">
          These details help narrow down where your item may have been found.
        </p>

        <div className="mt-7 space-y-6">
          <div className="grid grid-cols-1 gap-6 sm:grid-cols-2 sm:items-start">
            <WizardField id="dateLost" label="Date Lost" required error={errors.dateLost?.message}>
              <Controller
                name="dateLost"
                control={control}
                render={({ field }) => (
                  <DateField
                    id="dateLost"
                    value={field.value}
                    onChange={field.onChange}
                    onBlur={field.onBlur}
                    hasError={!!errors.dateLost}
                    max={todayStr}
                  />
                )}
              />
            </WizardField>
            <p className="pt-8 text-sm text-gray-500 sm:pt-9">
              Select the date you last remember having the item.
            </p>
          </div>

          <WizardField
            id="lastKnownLocation"
            label="Last Known Location"
            required
            error={errors.lastKnownLocation?.message}
            hint="Enter the place where you last remember having the item."
          >
            <div className="relative">
              <MapPin className="pointer-events-none absolute left-4 top-1/2 h-[18px] w-[18px] -translate-y-1/2 text-gray-400" />
              <input
                id="lastKnownLocation"
                type="text"
                placeholder="e.g. Main Library, 2nd floor"
                {...register("lastKnownLocation")}
                className={wizardInputClass(!!errors.lastKnownLocation, "pl-11")}
              />
            </div>
          </WizardField>

          <div className="max-w-sm">
            <MapIllustration caption={location || "Your last known location"} />
          </div>
        </div>
      </div>

      <aside className="h-fit rounded-2xl border border-indigo-100 bg-indigo-50 p-6">
        <h3 className="text-lg font-bold text-gray-900">Location tips</h3>
        <ul className="mt-4 space-y-4">
          {TIPS.map(({ icon: Icon, text }, i) => (
            <li key={i} className="flex items-start gap-3 text-sm text-gray-600">
              <Icon className="mt-0.5 h-[18px] w-[18px] shrink-0 text-gray-400" />
              <span>{text}</span>
            </li>
          ))}
        </ul>
      </aside>
    </div>
  );
}
