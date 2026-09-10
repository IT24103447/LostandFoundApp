import { useFormContext, Controller } from "react-hook-form";
import { Lock, ShieldCheck, ImageIcon } from "lucide-react";
import type { ReportFoundItemFormValues } from "../../schemas/reportFoundItemSchema";
import { FOUND_ITEM_CATEGORIES, MAX_FOUND_ITEM_PHOTOS } from "../../schemas/reportFoundItemSchema";
import { WizardField, wizardInputClass } from "../WizardField";
import { PhotoDropzone } from "../PhotoDropzone";

function formatDisplay(value: string): string {
  if (!value) return "";
  const d = new Date(`${value}T00:00:00`);
  if (Number.isNaN(d.getTime())) return "";
  return d.toLocaleDateString(undefined, { year: "numeric", month: "long", day: "numeric" });
}

export function StepFoundVerification() {
  const {
    register,
    control,
    watch,
    formState: { errors },
  } = useFormContext<ReportFoundItemFormValues>();

  const values = watch();
  const categoryLabel =
    FOUND_ITEM_CATEGORIES.find((c) => c.value === values.category)?.label ?? values.category;

  return (
    <div className="space-y-6">
      <div className="grid grid-cols-1 gap-6 lg:grid-cols-2">
        <div className="rounded-2xl border border-gray-100 bg-white p-8 shadow-sm">
          <div className="mb-2 flex items-center justify-between">
            <h2 className="text-xl font-bold text-gray-900">Add a photo</h2>
            <span className="rounded-full bg-gray-100 px-2.5 py-1 text-xs font-medium text-gray-500">
              Optional
            </span>
          </div>
          <p className="mb-5 text-sm text-gray-500">
             A photo can help the owner recognize their belongings. Up to {MAX_FOUND_ITEM_PHOTOS} photo.
          </p>
          <Controller
            name="photos"
            control={control}
            render={({ field }) => (
              <PhotoDropzone photos={field.value ?? []} onChange={field.onChange} maxPhotos={MAX_FOUND_ITEM_PHOTOS}/>
            )}
          />
          {errors.photos?.message && (
            <p className="mt-3 text-sm text-red-500">{errors.photos.message as string}</p>
          )}
        </div>

        <div className="rounded-2xl border border-indigo-100 bg-indigo-50/50 p-8">
          <div className="mb-3 flex items-center justify-between">
            <div className="flex h-10 w-10 items-center justify-center rounded-xl bg-white shadow-sm">
              <Lock className="h-5 w-5 text-indigo-600" />
            </div>
            <span className="rounded-full bg-indigo-600 px-2 py-0.5 text-[10px] font-bold tracking-wide text-white">
              PRIVATE
            </span>
          </div>
          <h2 className="text-xl font-bold text-gray-900">Private Matching Information</h2>
          <p className="mt-1 text-sm leading-relaxed text-gray-600">
            This information will never be shown publicly. It is used only to help verify that a
            claimed item belongs to its owner.
          </p>

          <div className="mt-5">
            <WizardField
              id="hiddenInformation"
              label="Hidden information"
              required
              error={errors.hiddenInformation?.message}
            >
              <textarea
                id="hiddenInformation"
                rows={4}
                placeholder="Example: A small scratch, sticker, engraving, or unique detail you observed."
                {...register("hiddenInformation")}
                className={wizardInputClass(!!errors.hiddenInformation, "resize-none bg-white")}
              />
            </WizardField>
            <p className="mt-2 flex items-center gap-1.5 text-xs text-gray-500">
              <ShieldCheck className="h-3 w-3" />
              Used only for match verification
            </p>
          </div>
        </div>
      </div>

      <div className="rounded-2xl border border-gray-100 bg-white p-6 shadow-sm">
        <h3 className="mb-4 text-base font-bold text-gray-900">Review your report</h3>
        <div className="grid grid-cols-2 gap-y-3 text-sm sm:grid-cols-4">
          <div>
            <p className="font-semibold text-gray-900">{values.title || "—"}</p>
            <p className="text-gray-500">{categoryLabel || "—"}</p>
          </div>
          <div>
            <p className="text-gray-500">
              Found: <span className="text-gray-800">{formatDisplay(values.dateFound) || "—"}</span>
            </p>
            <p className="text-gray-500">
              Location: <span className="text-gray-800">{values.locationFound || "—"}</span>
            </p>
          </div>
          <div className="flex items-center gap-2 text-gray-500">
            <ImageIcon className="h-4 w-4" />
            Photos: <span className="text-gray-800">{values.photos?.length ?? 0} added</span>
          </div>
          <div>
            <p className="text-gray-500">Private verification</p>
            <p className="flex items-center gap-1 text-gray-800">
              <Lock className="h-3.5 w-3.5" />
              {values.hiddenInformation ? "Hidden" : "—"}
            </p>
          </div>
        </div>
      </div>
    </div>
  );
}
