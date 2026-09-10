import { useEffect, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import { useForm, Controller } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { Lock, Info } from "lucide-react";
import { AppHeader } from "../layout/AppHeader";
import { WizardField, wizardInputClass } from "../components/WizardField";
import { CategorySelect } from "../components/CategorySelect";
import { DateField } from "../components/DateField";
import { EditPhotoField } from "../components/EditPhotoField";
import { editFoundItemSchema, type EditFoundItemFormValues } from "../schemas/editFoundItemSchema";
import {
  getFoundItem,
  updateFoundItem,
  replaceFoundItemPhoto,
  deleteFoundItemPhoto,
  type FoundItemResponse,
} from "../api/reportFoundItem";

export function EditFoundItemPage() {
  const { id } = useParams<{ id: string }>();
  const navigate = useNavigate();

  const [item, setItem] = useState<FoundItemResponse | null>(null);
  const [loadError, setLoadError] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [saved, setSaved] = useState(false);

  const {
    register,
    control,
    handleSubmit,
    reset,
    watch,
    formState: { errors, isSubmitting },
  } = useForm<EditFoundItemFormValues>({
    resolver: zodResolver(editFoundItemSchema),
  });

  useEffect(() => {
    if (!id) return;
    const controller = new AbortController();
    getFoundItem(id, controller.signal)
      .then((data) => {
        setItem(data);
        reset({
          title: data.title,
          category: data.category,
          description: data.description,
          dateFound: data.dateFound,
          locationFound: data.locationFound,
          hiddenInformation: "",
        });
      })
      .catch((err) => {
        if (err instanceof DOMException && err.name === "AbortError") return;
        setLoadError("We couldn't load this report. It may not exist, or you may not have access to it.");
      });
    return () => controller.abort();
  }, [id, reset]);

  const todayStr = new Date().toISOString().split("T")[0];
  const description = watch("description") ?? "";

  const onSubmit = async (values: EditFoundItemFormValues) => {
    if (!id) return;
    setSubmitError(null);
    setSaved(false);
    try {
      const updated = await updateFoundItem(id, values);
      setItem(updated);
      setSaved(true);
    } catch (err) {
      const status = (err as { status?: number }).status;
      if (status === 403) {
        setSubmitError("You don't have permission to edit this report.");
      } else if (status === 400) {
        setSubmitError("Please check the highlighted fields and try again.");
      } else {
        setSubmitError("Something went wrong saving your changes. Please try again.");
      }
    }
  };

  const handleReplacePhoto = async (file: File) => {
    if (!id) return;
    const updated = await replaceFoundItemPhoto(id, file);
    setItem(updated);
  };

  const handleDeletePhoto = async () => {
    if (!id) return;
    const updated = await deleteFoundItemPhoto(id);
    setItem(updated);
  };

  if (loadError) {
    return (
      <div className="min-h-screen bg-[#FAFAFC]">
        <AppHeader />
        <div className="mx-auto max-w-3xl px-6 py-16 text-center">
          <p className="text-gray-600">{loadError}</p>
          <button
            type="button"
            onClick={() => navigate("/my-reports")}
            className="mt-4 rounded-xl bg-indigo-600 px-5 py-2.5 text-sm font-semibold text-white hover:bg-indigo-700"
          >
            Back to My Reports
          </button>
        </div>
      </div>
    );
  }

  if (!item) {
    return (
      <div className="min-h-screen bg-[#FAFAFC]">
        <AppHeader />
        <div className="mx-auto max-w-3xl px-6 py-16 text-center text-gray-500">Loading…</div>
      </div>
    );
  }

  return (
    <div className="min-h-screen bg-[#FAFAFC]">
      <AppHeader />
      <div className="mx-auto max-w-3xl px-6 py-10">
        <h1 className="text-2xl font-bold text-gray-900">Edit Found Item Report</h1>
        <p className="mt-1.5 text-[15px] text-gray-500">Update your report's details below.</p>

        <form onSubmit={handleSubmit(onSubmit)} className="mt-8 space-y-6">
          <div className="rounded-2xl border border-gray-100 bg-white p-8 shadow-sm">
            <div className="grid grid-cols-1 gap-6 sm:grid-cols-2">
              <WizardField id="title" label="Item title" required error={errors.title?.message}>
                <input
                  id="title"
                  type="text"
                  {...register("title")}
                  className={wizardInputClass(!!errors.title)}
                />
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
                trailing={<span className="text-xs text-gray-400">{description.length} / 2000</span>}
              >
                <textarea
                  id="description"
                  rows={4}
                  {...register("description")}
                  className={wizardInputClass(!!errors.description, "resize-none")}
                />
              </WizardField>
            </div>

            <div className="mt-6 grid grid-cols-1 gap-6 sm:grid-cols-2">
              <WizardField id="dateFound" label="Date Found" required error={errors.dateFound?.message}>
                <Controller
                  name="dateFound"
                  control={control}
                  render={({ field }) => (
                    <DateField
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

              <WizardField id="locationFound" label="Location Found" required error={errors.locationFound?.message}>
                <input
                  id="locationFound"
                  type="text"
                  {...register("locationFound")}
                  className={wizardInputClass(!!errors.locationFound)}
                />
              </WizardField>
            </div>
          </div>

          <div className="rounded-2xl border border-indigo-100 bg-indigo-50/50 p-8">
            <div className="mb-3 flex h-10 w-10 items-center justify-center rounded-xl bg-white shadow-sm">
              <Lock className="h-5 w-5 text-indigo-600" />
            </div>
            <div className="mb-1 flex items-center gap-2">
              <h2 className="text-xl font-bold text-gray-900">Private Matching Information</h2>
              <span className="rounded-full bg-indigo-600 px-2 py-0.5 text-[10px] font-bold tracking-wide text-white">PRIVATE</span>
            </div>
            <p className="text-sm leading-relaxed text-gray-600">
              This is never shown publicly. For your security, it isn't pre-filled — re-enter it below,
              changed or unchanged, to save your report.
            </p>
            <div className="mt-5">
              <WizardField
                id="hiddenInformation"
                label="Hidden information"
                required
                error={errors.hiddenInformation?.message}
                trailing={
                  <span className="flex items-center gap-1 text-xs text-gray-500">
                    <Info className="h-3.5 w-3.5" />
                    Used only for secure matching
                  </span>
                }
              >
                <textarea
                  id="hiddenInformation"
                  rows={3}
                  {...register("hiddenInformation")}
                  className={wizardInputClass(!!errors.hiddenInformation, "resize-none bg-white")}
                />
              </WizardField>
            </div>
          </div>

          <div className="rounded-2xl border border-gray-100 bg-white p-8 shadow-sm">
            <h2 className="text-xl font-bold text-gray-900">Photo</h2>
            <p className="mt-1 text-sm text-gray-500">One photo per report. Changes save immediately.</p>
            <div className="mt-5">
              <EditPhotoField
                photo={item.photo}
                onReplace={handleReplacePhoto}
                onDelete={handleDeletePhoto}
              />
            </div>
          </div>

          {submitError && (
            <div className="rounded-xl border border-red-200 bg-red-50 px-4 py-3 text-sm text-red-700">
              {submitError}
            </div>
          )}
          {saved && !submitError && (
            <div className="rounded-xl border border-emerald-200 bg-emerald-50 px-4 py-3 text-sm text-emerald-700">
              Changes saved.
            </div>
          )}

          <div className="flex items-center justify-end gap-3 border-t border-gray-100 pt-6">
            <button
              type="button"
              onClick={() => navigate("/my-reports")}
              className="rounded-xl border border-gray-200 bg-white px-6 py-3 text-[15px] font-semibold text-gray-800 hover:bg-gray-50"
            >
              Back
            </button>
            <button
              type="submit"
              disabled={isSubmitting}
              className="rounded-xl bg-indigo-600 px-6 py-3 text-[15px] font-semibold text-white hover:bg-indigo-700 disabled:opacity-60"
            >
              {isSubmitting ? "Saving…" : "Save Changes"}
            </button>
          </div>
        </form>
      </div>
    </div>
  );
}