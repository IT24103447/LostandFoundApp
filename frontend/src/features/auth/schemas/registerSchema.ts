import { z } from "zod";

export const registerSchema = z
  .object({
    name: z
      .string()
      .min(1, "Name is required.")
      .max(150, "Name must be at most 150 characters."),
    email: z
      .string()
      .min(1, "Email is required.")
      .email("Enter a valid email address.")
      .max(254, "Email must be at most 254 characters."),
    phoneNo: z
      .string()
      .min(1, "Phone number is required.")
      .regex(
        /^\+[1-9]\d{6,14}$/,
        "Phone must be in E.164 format with a leading + (e.g. +94771234567).",
      ),
    password: z
      .string()
      .min(8, "Password must be at least 8 characters.")
      .max(128, "Password must be at most 128 characters.")
      .regex(/[A-Z]/, "Password must contain at least one uppercase letter.")
      .regex(/[a-z]/, "Password must contain at least one lowercase letter.")
      .regex(/[0-9]/, "Password must contain at least one digit."),
    confirmPassword: z.string().min(1, "Please confirm your password."),
  })
  .refine((v) => v.password === v.confirmPassword, {
    path: ["confirmPassword"],
    message: "Passwords do not match.",
  });

export type RegisterFormValues = z.infer<typeof registerSchema>;
