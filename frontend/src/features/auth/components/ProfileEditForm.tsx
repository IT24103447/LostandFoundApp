import { useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { profileSchema, type ProfileFormValues } from "../schemas/profileSchema";
import { updateProfile, type UserProfile } from "../api/auth";
import type { ApiError } from "../../../lib/apiClient";
import { Field } from "./Field";
import { inputClass, isValidationProblem } from "./helpers";

type ProfileEditFormProps = {
  user: UserProfile;
  onSaved: (updated: UserProfile) => void;
  onCancel: () => void;
};

export function ProfileEditForm({ user, onSaved, onCancel }: ProfileEditFormProps) {
  const {
    register: registerField,
    handleSubmit,
    setError,
    formState: { errors, isSubmitting, isSubmitted, touchedFields },
  } = useForm<ProfileFormValues>({
    resolver: zodResolver(profileSchema),
    mode: "onTouched",
    reValidateMode: "onChange",
    defaultValues: {
      name: user.name,
      phoneNo: user.phoneNo,
    },
  });

  const [submitError, setSubmitError] = useState<string | null>(null);

  const showError = (name: keyof ProfileFormValues): boolean => {
    if (isSubmitted) return !!errors[name];
    if (!touchedFields[name]) return false;
    return !!errors[name];
  };

  const onSubmit = async (values: ProfileFormValues) => {
    setSubmitError(null);

    const controller = new AbortController();
    try {
      const updated = await updateProfile(values, controller.signal);
      onSaved(updated);
    } catch (err) {
      const apiErr = err as ApiError;
      if (apiErr.status === 400 && isValidationProblem(apiErr.body)) {
        for (const [field, messages] of Object.entries(apiErr.body.errors) as Array<
          [string, string[]]
        >) {
          const lcField = field.charAt(0).toLowerCase() + field.slice(1);
          setError(lcField as keyof ProfileFormValues, {
            type: "server",
            message: messages.join(" "),
          });
        }
      } else if (apiErr.status === 409) {
        const body = apiErr.body as { error?: string } | null;
        const msg = body?.error ?? "Phone number already in use.";
        if (msg.toLowerCase().includes("phone")) {
          setError("phoneNo", { type: "server", message: msg });
        } else {
          setSubmitError(msg);
        }
      } else if (apiErr.status === 429) {
        setSubmitError("Too many attempts. Please wait a minute and try again.");
      } else {
        setSubmitError("Something went wrong. Please try again.");
      }
    }
  };

  return (
    <form onSubmit={handleSubmit(onSubmit)} className="space-y-4" noValidate>
      <Field
        id="name"
        label="Full name"
        error={showError("name") ? errors.name?.message : undefined}
      >
        <input
          id="name"
          type="text"
          autoComplete="name"
          {...registerField("name")}
          className={inputClass(showError("name") && !!errors.name)}
        />
      </Field>

      <div>
        <label className="block text-sm font-medium text-gray-700">Email</label>
        <p className="mt-1 text-sm text-gray-500 bg-gray-50 rounded-md border border-gray-200 px-3 py-2">
          {user.email}
        </p>
      </div>

      <Field
        id="phoneNo"
        label="Phone number"
        hint="e.g. +94771234567"
        error={showError("phoneNo") ? errors.phoneNo?.message : undefined}
      >
        <input
          id="phoneNo"
          type="text"
          inputMode="tel"
          autoComplete="tel"
          {...registerField("phoneNo")}
          className={inputClass(showError("phoneNo") && !!errors.phoneNo)}
        />
      </Field>

      {submitError && (
        <p className="text-sm text-red-500 font-medium" role="alert">
          {submitError}
        </p>
      )}

      <div className="flex gap-3 pt-2">
        <button
          type="submit"
          disabled={isSubmitting}
          className="flex-1 flex justify-center items-center rounded-lg bg-indigo-600 px-4 py-2 text-sm font-medium text-white shadow-sm transition-colors hover:bg-indigo-700 disabled:opacity-60 disabled:cursor-not-allowed"
        >
          {isSubmitting ? (
            <>
              <svg className="animate-spin -ml-1 mr-2 h-4 w-4 text-white" xmlns="http://www.w3.org/2000/svg" fill="none" viewBox="0 0 24 24">
                <circle className="opacity-25" cx="12" cy="12" r="10" stroke="currentColor" strokeWidth="4"></circle>
                <path className="opacity-75" fill="currentColor" d="M4 12a8 8 0 018-8V0C5.373 0 0 5.373 0 12h4zm2 5.291A7.962 7.962 0 014 12H0c0 3.042 1.135 5.824 3 7.938l3-2.647z"></path>
              </svg>
              Saving…
            </>
          ) : (
            "Save changes"
          )}
        </button>
        <button
          type="button"
          onClick={onCancel}
          disabled={isSubmitting}
          className="flex-1 rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50 disabled:opacity-60"
        >
          Cancel
        </button>
      </div>
    </form>
  );
}
