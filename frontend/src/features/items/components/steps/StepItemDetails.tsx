import { useFormContext, Controller } from "react-hook-form";
import { Package, Lightbulb, Tag, Search } from "lucide-react";
import type { ReportLostItemFormValues } from "../../schemas/reportLostItemSchema";
import { WizardField, wizardInputClass } from "../WizardField";
import { CategorySelect } from "../CategorySelect";
import { ItemsIllustration } from "../Illustrations";

const TIPS = [
  { icon: Lightbulb, text: "Include the brand and model" },
  { icon: Tag, text: "Describe unique marks or features" },
  { icon: Search, text: "Add useful details others can recognize" },
];

export function StepItemDetails() {
  const {
    register,
    control,
    watch,
    formState: { errors },
  } = useFormContext<ReportLostItemFormValues>();

  const description = watch("description") ?? "";

  return (
    <div className="grid grid-cols-1 gap-6 lg:grid-cols-[1fr_320px]">
      <div className="rounded-2xl border border-gray-100 bg-white p-8 shadow-sm">
        <h2 className="text-2xl font-bold text-gray-900">Tell us about your item</h2>
        <p className="mt-1.5 text-[15px] text-gray-500">
          Give us enough detail to help identify your belongings.
        </p>

        <div className="mt-7 grid grid-cols-1 gap-6 sm:grid-cols-2">
          <WizardField id="title" label="Item title" required error={errors.title?.message}>
            <div className="relative">
              <Package className="pointer-events-none absolute left-4 top-1/2 h-[18px] w-[18px] -translate-y-1/2 text-gray-400" />
              <input
                id="title"
                type="text"
                placeholder="e.g. Black leather wallet"
                {...register("title")}
                className={wizardInputClass(!!errors.title, "pl-11")}
              />
            </div>
          </WizardField>

          <WizardField id="category" label="Category" required error={errors.category?.message}>
            <Controller
              name="category"
              control={control}
              render={({ field }) => (
                <CategorySelect
                  id="category"
                  value={field.value}
                  onChange={field.onChange}
                  onBlur={field.onBlur}
                  hasError={!!errors.category}
                />
              )}
            />
          </WizardField>
        </div>

        <div className="mt-6">
          <WizardField
            id="description"
            label="Description"
            required
            error={errors.description?.message}
            hint="Describe the item's color, brand, appearance, unique features, or anything else that may help identify it."
            trailing={<span className="text-xs text-gray-400">{description.length} / 1000</span>}
          >
            <textarea
              id="description"
              rows={4}
              placeholder="Describe your item in detail…"
              {...register("description")}
              className={wizardInputClass(!!errors.description, "resize-none")}
            />
          </WizardField>
        </div>
      </div>

      <aside className="h-fit rounded-2xl border border-indigo-100 bg-indigo-50 p-6">
        <h3 className="text-lg font-bold text-gray-900">Tips for a better report</h3>
        <ul className="mt-4 space-y-4">
          {TIPS.map(({ icon: Icon, text }, i) => (
            <li key={i} className="flex items-start gap-3 text-sm text-gray-600">
              <Icon className="mt-0.5 h-[18px] w-[18px] shrink-0 text-gray-400" />
              <span>{text}</span>
            </li>
          ))}
        </ul>
        <div className="mt-6">
          <ItemsIllustration />
        </div>
      </aside>
    </div>
  );
}
