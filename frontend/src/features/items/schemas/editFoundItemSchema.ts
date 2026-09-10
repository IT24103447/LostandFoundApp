import { z } from "zod";

const today = () => new Date(new Date().toDateString());

export const editFoundItemSchema = z.object({
  title: z.string().trim().min(1, "Item title is required.").max(150, "Title must be at most 150 characters."),
  category: z.string().trim().min(1, "Please select a category."),
  description: z.string().trim().min(1, "Description is required.").max(2000, "Description must be at most 2000 characters."),
  dateFound: z
    .string()
    .min(1, "Date found is required.")
    .refine((v) => !Number.isNaN(Date.parse(v)), "Enter a valid date.")
    .refine((v) => new Date(v) <= today(), "Date found cannot be in the future."),
  locationFound: z.string().trim().min(1, "Location found is required.").max(255, "Location found must be at most 255 characters."),
  hiddenInformation: z.string().trim().min(1, "Hidden information is required.").max(500, "Hidden information must be at most 500 characters."),
});
export type EditFoundItemFormValues = z.infer<typeof editFoundItemSchema>;