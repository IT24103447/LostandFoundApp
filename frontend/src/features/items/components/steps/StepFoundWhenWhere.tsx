import { useFormContext, Controller } from "react-hook-form";
import { MapPin, Building2, LayoutGrid, Navigation } from "lucide-react";
import type { ReportFoundItemFormValues } from "../../schemas/reportFoundItemSchema";
import { WizardField, wizardInputClass } from "../WizardField";
import { FoundDateField } from "../FoundDateField";
import { FoundMapIllustration } from "../FoundIllustrations";

const TIPS = [
  { icon: Building2, text: "Use a recognizable building or area" },
  { icon: LayoutGrid, text: "Include room, floor or section when useful" },
  { icon: Navigation, text: "Example: Library, 2nd Floor" },
];

export function StepFoundWhenWhere() {
  const {
    register,
    control,
    formState: { errors },
  } = useFormContext<ReportFoundItemFormValues>();

  const todayStr = new Date().toISOString().split("T")[0];

  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[1fr_320px]">
      <div className="rounded-2xl border border-gray-100 bg-white p-8 shadow-sm">
        <h2 className="text-2xl font-bold text-gray-900">Found details</h2>
        <p className="mt-1.5 text-[15px] text-gray-500">
          Accurate time and location information can help the owner understand where their item was
          recovered.
        </p>

        <div className="mt-7 grid grid-cols-1 gap-8 lg:grid-cols-[1fr_260px] lg:items-start">
          <div className="space-y-6">
            <WizardField id="dateFound" label="Date Found" required error={errors.dateFound?.message}>
              <Controller
                name="dateFound"
                control={control}
                render={({ field }) => (
                  <FoundDateField
                    id="dateFound"
                    value={field.value}
                    onChange={field.onChange}
                    onBlur={field.onBlur}
                    hasError={!!errors.dateFound}
                    max={todayStr}
                  />
                )}
              />
            </WizardField>

            <WizardField
              id="locationFound"
              label="Location Found"
              required
              error={errors.locationFound?.message}
              hint="Enter the place where you found the item."
            >
              <div className="relative">
                <MapPin className="pointer-events-none absolute left-4 top-4 h-[18px] w-[18px] text-gray-400" />
                <textarea
                  id="locationFound"
                  rows={4}
                  placeholder="e.g. SLIIT Library, Malabe Campus"
                  {...register("locationFound")}
                  className={wizardInputClass(!!errors.locationFound, "resize-none pl-11 pt-3.5")}
                />
              </div>
            </WizardField>
          </div>

          <FoundMapIllustration />
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
