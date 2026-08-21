import { useState } from "react";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import {
  registerSchema,
  type RegisterFormValues,
} from "../schemas/registerSchema";
import { register } from "../api/register";
import type { ApiError } from "../../../lib/apiClient";
import { Field } from "../../../shared/components/Field";
import { inputClass, isValidationProblem } from "./helpers";

export function RegisterForm() {
  const {
    register: registerField,
    handleSubmit,
    setError,
    reset,
    formState: { errors, isSubmitting, isSubmitted, touchedFields },
  } = useForm<RegisterFormValues>({
    resolver: zodResolver(registerSchema),
    mode: "onTouched",
    reValidateMode: "onChange",
  });

  const [success, setSuccess] = useState<string | null>(null);
  const [submitError, setSubmitError] = useState<string | null>(null);

  // Industry-standard feedback gate: show this field's error only after the user has
  // submitted the form, or after they've touched + blurred that field. Prevents the
  // "red border appears when clicking the next field" UX trap.
  const showError = (name: keyof RegisterFormValues): boolean =>
    isSubmitted || !!touchedFields[name];

  const onSubmit = async (values: RegisterFormValues) => {
    setSuccess(null);
    setSubmitError(null);

    const controller = new AbortController();
    try {
      const { confirmPassword: _confirm, ...payload } = values;
      const result = await register(payload, controller.signal);
      setSuccess(`Account created for ${result.email}. You can now sign in.`);
      reset({
        name: "",
        email: "",
        phoneNo: "",
        password: "",
        confirmPassword: "",
      });
    } catch (err) {
      const apiErr = err as ApiError;
      if (apiErr.status === 400 && isValidationProblem(apiErr.body)) {
        for (const [field, messages] of Object.entries(apiErr.body.errors) as Array<
          [string, string[]]
        >) {
          const lcField = field.charAt(0).toLowerCase() + field.slice(1);
          setError(lcField as keyof RegisterFormValues, {
            type: "server",
            message: messages.join(" "),
          });
        }
      } else if (apiErr.status === 409) {
        const body = apiErr.body as { error?: string } | null;
        const msg = body?.error ?? "An account with these details already exists.";
        if (msg.toLowerCase().includes("email")) {
          setError("email", { type: "server", message: msg });
        } else if (msg.toLowerCase().includes("phone")) {
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

      <Field
        id="email"
        label="Email"
        error={showError("email") ? errors.email?.message : undefined}
      >
        <input
          id="email"
          type="email"
          autoComplete="email"
          {...registerField("email")}
          className={inputClass(showError("email") && !!errors.email)}
        />
  </Field>

      <Field
        id="phoneNo"
        label="Phone number"
        hint="E.164 format, e.g. +94771234567"
        error={showError("phoneNo") ? errors.phoneNo?.message : undefined}
      >
        <input
          id="phoneNo"
          type="tel"
          autoComplete="tel"
          {...registerField("phoneNo")}
          className={inputClass(showError("phoneNo") && !!errors.phoneNo)}
        />
  </Field>

      <Field
        id="password"
        label="Password"
        hint="8–128 chars, at least one upper, lower, and digit."
        error={showError("password") ? errors.password?.message : undefined}
      >
        <input
          id="password"
          type="password"
          autoComplete="new-password"
          {...registerField("password")}
          className={inputClass(showError("password") && !!errors.password)}
        />
  </Field>

      <Field
        id="confirmPassword"
        label="Confirm password"
        error={showError("confirmPassword") ? errors.confirmPassword?.message : undefined}
      >
        <input
          id="confirmPassword"
          type="password"
          autoComplete="new-password"
          {...registerField("confirmPassword")}
          className={inputClass(showError("confirmPassword") && !!errors.confirmPassword)}
        />
  </Field>

      <button
        type="submit"
        disabled={isSubmitting}
        className="w-full rounded-md bg-blue-600 px-4 py-2 text-sm font-medium text-white shadow-sm hover:bg-blue-700 focus:outline-none focus:ring-2 focus:ring-blue-500 focus:ring-offset-2 disabled:cursor-not-allowed disabled:opacity-60"
      >
        {isSubmitting ? "Creating account…" : "Create account"}
   </button>

      {success && (
        <p className="text-sm text-green-700" role="status">
          {success}
     </p>
      )}
      {submitError && (
        <p className="text-sm text-red-700" role="alert">
          {submitError}
     </p>
      )}
 </form>
  );
}
