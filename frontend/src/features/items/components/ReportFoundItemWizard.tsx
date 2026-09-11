import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { FormProvider, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { ArrowLeft, ArrowRight } from "lucide-react";
import {
  reportFoundItemSchema,
  foundStep1Schema,
  foundStep2Schema,
  foundStep3Schema,
  defaultReportFoundItemValues,
  type ReportFoundItemFormValues,
} from "../schemas/reportFoundItemSchema";
import { reportFoundItem } from "../api/reportFoundItem";
import type { ApiError } from "../../../lib/apiClient";
import type { WizardStep } from "./StepIndicator";
import { FoundStepIndicator } from "./FoundStepIndicator";
import { StepFoundItemDetails } from "./steps/StepFoundItemDetails";
import { StepFoundWhenWhere } from "./steps/StepFoundWhenWhere";
import { StepFoundVerification } from "./steps/StepFoundVerification";

const STEP_FIELDS: Record<WizardStep, (keyof ReportFoundItemFormValues)[]> = {
  1: Object.keys(foundStep1Schema.shape) as (keyof ReportFoundItemFormValues)[],
  2: Object.keys(foundStep2Schema.shape) as (keyof ReportFoundItemFormValues)[],
  3: Object.keys(foundStep3Schema.shape) as (keyof ReportFoundItemFormValues)[],
};

const STEP_HEADINGS: Record<WizardStep, { title: string; subtitle: string }> = {
  1: {
    title: "Report a Found Item",
    subtitle: "Tell us about the item you found so we can help reunite it with its owner.",
  },
  2: { title: "When & Where", subtitle: "Tell us when and where you found the item." },
  3: {
    title: "One last step",
    subtitle: "Add a photo and a private detail that can help us verify the item's owner.",
  },
};

export function ReportFoundItemWizard() {
  const navigate = useNavigate();
  const [step, setStep] = useState<WizardStep>(1);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const methods = useForm<ReportFoundItemFormValues>({
    resolver: zodResolver(reportFoundItemSchema),
    defaultValues: defaultReportFoundItemValues,
    mode: "onTouched",
  });

  const { trigger, handleSubmit, setError } = methods;

  const goNext = async () => {
    const valid = await trigger(STEP_FIELDS[step]);
    if (!valid) return;
    setStep((s) => (s < 3 ? ((s + 1) as WizardStep) : s));
  };

  const goBack = () => {
    setStep((s) => (s > 1 ? ((s - 1) as WizardStep) : s));
  };

  const onSubmit = async (values: ReportFoundItemFormValues) => {
    setSubmitError(null);
    setIsSubmitting(true);
    const controller = new AbortController();
    try {
      const result = await reportFoundItem(
        {
          title: values.title,
          category: values.category,
          description: values.description,
          dateFound: values.dateFound,
          locationFound: values.locationFound,
          hiddenInformation: values.hiddenInformation,
          photos: values.photos,
        },
        controller.signal,
      );
      navigate("/report-found-item/success", { state: { report: result } });
    } catch (err) {
      const apiErr = err as ApiError;
      if (
        (apiErr.status === 400 || apiErr.status === 422) &&
        typeof apiErr.body === "object" &&
        apiErr.body !== null &&
        "errors" in apiErr.body
      ) {
        const fieldErrors = (apiErr.body as { errors: Record<string, string[]> }).errors;
        let firstInvalidStep: WizardStep | null = null;
        for (const [field, messages] of Object.entries(fieldErrors)) {
          const key = (field.charAt(0).toLowerCase() + field.slice(1)) as keyof ReportFoundItemFormValues;
          setError(key, { type: "server", message: messages.join(" ") });
          const stepForField = (Object.keys(STEP_FIELDS).map(Number) as WizardStep[]).find((s) =>
            STEP_FIELDS[s].includes(key),
          );
          if (stepForField && (firstInvalidStep === null || stepForField < firstInvalidStep)) {
            firstInvalidStep = stepForField;
          }
        }
        if (firstInvalidStep) setStep(firstInvalidStep);
      } else if (apiErr.status === 401) {
        setSubmitError("Your session has expired. Please sign in again.");
      } else {
        setSubmitError("Something went wrong submitting your report. Please try again.");
      }
    } finally {
      setIsSubmitting(false);
    }
  };

  const heading = STEP_HEADINGS[step];

  return (
    <FormProvider {...methods}>
      <form onSubmit={handleSubmit(onSubmit)} noValidate>
        <div className="mx-auto max-w-[1600px] px-6 pb-16 pt-8 lg:px-10">
          <p className="text-sm text-gray-400">
            <button type="button" onClick={() => navigate("/my-reports")} className="hover:text-gray-600">
              My Reports
            </button>{" "}
            / Report Lost Item
          </p>

          <div className="mt-3 flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
            <div>
              <h1 className="text-3xl font-extrabold tracking-tight text-gray-900">{heading.title}</h1>
              <p className="mt-1.5 text-[15px] text-gray-500">{heading.subtitle}</p>
            </div>
            <FoundStepIndicator current={step} />
          </div>

          <hr className="my-6 border-gray-200" />

          {step === 1 && <StepFoundItemDetails />}
          {step === 2 && <StepFoundWhenWhere />}
          {step === 3 && <StepFoundVerification />}

          {submitError && (
            <p className="mt-4 text-sm text-red-500" role="alert">
              {submitError}
            </p>
          )}

          <div className="mt-8 flex items-center justify-between">
            <span className="text-sm text-gray-400">Step {step} of 3</span>
            <div className="flex items-center gap-3">
              {step === 1 && (
                <button
                  type="button"
                  onClick={() => navigate("/")}
                  className="rounded-xl border border-gray-200 bg-white px-5 py-2.5 text-sm font-semibold text-gray-700 shadow-sm transition-colors hover:bg-gray-50"
                >
                  Cancel
                </button>
              )}
              {step > 1 && (
                <button
                  type="button"
                  onClick={goBack}
                  className="flex items-center gap-1.5 rounded-xl border border-gray-200 bg-white px-5 py-2.5 text-sm font-semibold text-gray-700 shadow-sm transition-colors hover:bg-gray-50"
                >
                  <ArrowLeft className="h-4 w-4" />
                  Back
                </button>
              )}
              {step < 3 ? (
                <button
                  type="button"
                  onClick={goNext}
                  className="flex items-center gap-1.5 rounded-xl bg-indigo-600 px-5 py-2.5 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-indigo-700"
                >
                  Continue
                  <ArrowRight className="h-4 w-4" />
                </button>
              ) : (
                <button
                  type="submit"
                  disabled={isSubmitting}
                  className="flex items-center gap-1.5 rounded-xl bg-gradient-to-r from-indigo-600 to-purple-600 px-5 py-2.5 text-sm font-semibold text-white shadow-sm transition-colors hover:opacity-95 disabled:opacity-60"
                >
                  {isSubmitting ? "Submitting…" : "Report Found Item"}
                </button>
              )}
            </div>
          </div>
        </div>
      </form>
    </FormProvider>
  );
}
