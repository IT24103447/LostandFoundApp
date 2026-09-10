import { z } from "zod";

const today = () => new Date(new Date().toDateString());

export const editLostItemSchema = z.object({
  title: z.string().trim().min(1, "Item title is required.").max(150, "Title must be at most 150 characters."),
  category: z.string().trim().min(1, "Please select a category."),
  description: z.string().trim().min(1, "Description is required.").max(2000, "Description must be at most 2000 characters."),
  dateLost: z
    .string()
    .min(1, "Date lost is required.")
    .refine((v) => !Number.isNaN(Date.parse(v)), "Enter a valid date.")
    .refine((v) => new Date(v) <= today(), "Date lost cannot be in the future."),
  lastKnownLocation: z.string().trim().min(1, "Last known location is required.").max(255, "Last known location must be at most 255 characters."),
  hiddenInformation: z.string().trim().min(1, "Hidden information is required.").max(500, "Hidden information must be at most 500 characters."),
});
export type EditLostItemFormValues = z.infer<typeof editLostItemSchema>;