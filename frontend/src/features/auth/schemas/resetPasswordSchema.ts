import { z } from "zod";

export const resetPasswordSchema = z
  .object({
    code: z
      .string()
      .min(1, "Code is required.")
      .regex(/^\d{6}$/, "Code must be exactly 6 digits."),
    newPassword: z
      .string()
      .min(8, "Password must be at least 8 characters.")
      .max(128, "Password must be at most 128 characters.")
      .regex(/[A-Z]/, "Password must contain at least one uppercase letter.")
      .regex(/[a-z]/, "Password must contain at least one lowercase letter.")
      .regex(/[0-9]/, "Password must contain at least one digit."),
    confirmPassword: z.string().min(1, "Please confirm your password."),
  })
  .refine((v) => v.newPassword === v.confirmPassword, {
    path: ["confirmPassword"],
    message: "Passwords do not match.",
  });

export type ResetPasswordFormValues = z.infer<typeof resetPasswordSchema>;
