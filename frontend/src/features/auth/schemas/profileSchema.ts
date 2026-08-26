import { z } from "zod";

export const profileSchema = z.object({
  name: z
    .string()
    .min(1, "Name is required.")
    .max(150, "Name must be at most 150 characters."),
  phoneNo: z
    .string()
    .min(1, "Phone number is required.")
    .regex(
      /^\+[1-9]\d{6,14}$/,
      "Phone must be in E.164 format with a leading + (e.g. +94771234567).",
    ),
});

export type ProfileFormValues = z.infer<typeof profileSchema>;
