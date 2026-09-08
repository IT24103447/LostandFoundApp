import { z } from "zod";
import {
  LOST_ITEM_CATEGORIES,
  MAX_PHOTO_SIZE_BYTES,
  ALLOWED_PHOTO_TYPES,
} from "./reportLostItemSchema";

// Found items reuse the same category taxonomy as lost items so the Matching
// Service is comparing like-for-like categories between the two record types.
export const FOUND_ITEM_CATEGORIES = LOST_ITEM_CATEGORIES;

// The found-item form only supports a single photo (see prototype + Scenario 2
// of the "Report a Found Item" story, which is written in the singular).
export const MAX_FOUND_ITEM_PHOTOS = 1;

const today = () => new Date(new Date().toDateString());

const photoFileSchema = z.custom<File>((f) => f instanceof File);

// ---- Step 1: Found Item Details ----
export const foundStep1Schema = z.object({
  title: z
    .string()
    .min(1, "Item title is required.")
    .max(150, "Title must be at most 150 characters."),
  category: z.string().min(1, "Please select a category."),
  description: z
    .string()
    .min(1, "Description is required.")
    .max(500, "Description must be at most 500 characters."),
});
export type FoundStep1Values = z.infer<typeof foundStep1Schema>;

// ---- Step 2: When & Where ----
export const foundStep2Schema = z.object({
  dateFound: z
    .string()
    .min(1, "Date found is required.")
    .refine((v) => !Number.isNaN(Date.parse(v)), "Enter a valid date.")
    .refine((v) => new Date(v) <= today(), "Date found cannot be in the future."),
  locationFound: z
    .string()
    .min(1, "Location found is required.")
    .max(255, "Location found must be at most 255 characters."),
});
export type FoundStep2Values = z.infer<typeof foundStep2Schema>;

// ---- Step 3: Verification ----
// NOTE: hiddenInformation is a distinguishing detail only the real owner would
// know. It is stored on the record and published to Kafka for the Matching
// Service, but is never returned by any frontend-facing endpoint — see
// FoundItemResponse in ../api/reportFoundItem.ts, which deliberately has no
// field for it.
export const foundStep3Schema = z.object({
  hiddenInformation: z
    .string()
    .min(1, "Hidden information is required.")
    .max(500, "Hidden information must be at most 500 characters."),
  photos: z
    .array(photoFileSchema)
    .max(MAX_FOUND_ITEM_PHOTOS, "You can attach at most 1 photo.")
    .refine((files) => files.every((f) => f.size <= MAX_PHOTO_SIZE_BYTES), {
      message: "Each photo must be at most 5 MB.",
    })
    .refine((files) => files.every((f) => ALLOWED_PHOTO_TYPES.includes(f.type)), {
      message: "Photos must be JPEG, PNG, or WEBP images.",
    }),
});
export type FoundStep3Values = z.infer<typeof foundStep3Schema>;

// ---- Full form (all steps combined) ----
export const reportFoundItemSchema = foundStep1Schema
  .extend(foundStep2Schema.shape)
  .extend(foundStep3Schema.shape);
export type ReportFoundItemFormValues = z.infer<typeof reportFoundItemSchema>;

export const defaultReportFoundItemValues: ReportFoundItemFormValues = {
  title: "",
  category: "",
  description: "",
  dateFound: "",
  locationFound: "",
  hiddenInformation: "",
  photos: [],
};

export { ALLOWED_PHOTO_TYPES, MAX_PHOTO_SIZE_BYTES };
