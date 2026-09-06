import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { FormProvider, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { ArrowLeft, ArrowRight } from "lucide-react";
import {
  reportLostItemSchema,
  step1Schema,
  step2Schema,
  step3Schema,
  defaultReportLostItemValues,
  type ReportLostItemFormValues,
} from "../schemas/reportLostItemSchema";
import { reportLostItem } from "../api/reportLostItem";
import type { ApiError } from "../../../lib/apiClient";
import { StepIndicator, type WizardStep } from "./StepIndicator";
import { StepItemDetails } from "./steps/StepItemDetails";
import { StepWhenWhere } from "./steps/StepWhenWhere";
import { StepVerification } from "./steps/StepVerification";

const STEP_FIELDS: Record<WizardStep, (keyof ReportLostItemFormValues)[]> = {
  1: Object.keys(step1Schema.shape) as (keyof ReportLostItemFormValues)[],
  2: Object.keys(step2Schema.shape) as (keyof ReportLostItemFormValues)[],
  3: Object.keys(step3Schema.shape) as (keyof ReportLostItemFormValues)[],
};

const STEP_HEADINGS: Record<WizardStep, { title: string; subtitle: string }> = {
  1: { title: "Report a Lost Item", subtitle: "Let's start with the basic details about what you lost." },
  2: { title: "Where did you lose it?", subtitle: "Help us understand when and where you last had your item." },
  3: { title: "One last step", subtitle: "Add photos and a private detail that helps us verify a potential match." },
};

export function ReportLostItemWizard() {
  const navigate = useNavigate();
  const [step, setStep] = useState<WizardStep>(1);
  const [submitError, setSubmitError] = useState<string | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);

  const methods = useForm<ReportLostItemFormValues>({
    resolver: zodResolver(reportLostItemSchema),
    defaultValues: defaultReportLostItemValues,
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

  const onSubmit = async (values: ReportLostItemFormValues) => {
    setSubmitError(null);
    setIsSubmitting(true);
    const controller = new AbortController();
    try {
      const result = await reportLostItem(
        {
          title: values.title,
          category: values.category,
          description: values.description,
          dateLost: values.dateLost,
          lastKnownLocation: values.lastKnownLocation,
          hiddenInformation: values.hiddenInformation,
          photos: values.photos,
        },
        controller.signal,
      );
      navigate("/report-lost-item/success", { state: { report: result } });
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
          const key = (field.charAt(0).toLowerCase() + field.slice(1)) as keyof ReportLostItemFormValues;
          setError(key, { type: "server", message: messages.join(" ") });
          const stepForField = (Object.keys(STEP_FIELDS) as unknown as WizardStep[]).find((s) =>
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
            <button type="button" onClick={() => navigate("/")} className="hover:text-gray-600">
              Dashboard
            </button>{" "}
            / Report Lost Item
          </p>

          <div className="mt-3 flex flex-col gap-4 lg:flex-row lg:items-end lg:justify-between">
            <div>
              <h1 className="text-3xl font-extrabold tracking-tight text-gray-900">{heading.title}</h1>
              <p className="mt-1.5 text-[15px] text-gray-500">{heading.subtitle}</p>
            </div>
            <StepIndicator current={step} />
          </div>

          <hr className="my-6 border-gray-200" />

          {step === 1 && <StepItemDetails />}
          {step === 2 && <StepWhenWhere />}
          {step === 3 && <StepVerification />}

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
                  className="flex items-center gap-1.5 rounded-xl bg-indigo-600 px-5 py-2.5 text-sm font-semibold text-white shadow-sm transition-colors hover:bg-indigo-700 disabled:opacity-60"
                >
                  {isSubmitting ? "Submitting…" : "Report Lost Item"}
                </button>
              )}
            </div>
          </div>
        </div>
      </form>
    </FormProvider>
  );
}
