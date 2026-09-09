import { z } from "zod";
import {
  Smartphone,
  ShoppingBag,
  Shirt,
  Watch,
  FileText,
  KeyRound,
  MoreHorizontal,
  type LucideIcon,
} from "lucide-react";

export const MAX_PHOTOS = 5;
export const MAX_PHOTO_SIZE_BYTES = 5 * 1024 * 1024; // 5 MB
export const ALLOWED_PHOTO_TYPES = ["image/jpeg", "image/png", "image/webp"];

export type LostItemCategory = {
  value: string;
  label: string;
  icon: LucideIcon;
};

export const LOST_ITEM_CATEGORIES: LostItemCategory[] = [
  { value: "Electronics", label: "Electronics", icon: Smartphone },
  { value: "Bags", label: "Bags", icon: ShoppingBag },
  { value: "Clothing", label: "Clothing", icon: Shirt },
  { value: "Accessories", label: "Accessories", icon: Watch },
  { value: "Documents", label: "Documents", icon: FileText },
  { value: "Keys", label: "Keys", icon: KeyRound },
  { value: "Other", label: "Other", icon: MoreHorizontal },
];

const today = () => new Date(new Date().toDateString());

const photoFileSchema = z.custom<File>((f) => f instanceof File);

// ---- Step 1: Item Details ----
export const step1Schema = z.object({
  title: z
    .string()
    .trim()
    .min(1, "Item title is required.")
    .max(150, "Title must be at most 150 characters."),
  category: z.string().trim().min(1, "Please select a category."),
  description: z
    .string()
    .trim()
    .min(1, "Description is required.")
    .max(2000, "Description must be at most 2000 characters."),
});
export type Step1Values = z.infer<typeof step1Schema>;

// ---- Step 2: When & Where ----
export const step2Schema = z.object({
  dateLost: z
    .string()
    .min(1, "Date lost is required.")
    .refine((v) => !Number.isNaN(Date.parse(v)), "Enter a valid date.")
    .refine((v) => new Date(v) <= today(), "Date lost cannot be in the future."),
  lastKnownLocation: z
    .string()
    .trim()
    .min(1, "Last known location is required.")
    .max(255, "Last known location must be at most 255 characters."),
});
export type Step2Values = z.infer<typeof step2Schema>;

// ---- Step 3: Verification ----
export const step3Schema = z.object({
  hiddenInformation: z
    .string()
    .trim()
    .min(1, "Hidden information is required.")
    .max(500, "Hidden information must be at most 500 characters."),
  photos: z
    .array(photoFileSchema)
    .max(MAX_PHOTOS, `You can attach at most ${MAX_PHOTOS} photos.`)
    .refine((files) => files.every((f) => f.size <= MAX_PHOTO_SIZE_BYTES), {
      message: "Each photo must be at most 5 MB.",
    })
    .refine((files) => files.every((f) => ALLOWED_PHOTO_TYPES.includes(f.type)), {
      message: "Photos must be JPEG, PNG, or WEBP images.",
    }),
});
export type Step3Values = z.infer<typeof step3Schema>;

// ---- Full form (all steps combined) ----
export const reportLostItemSchema = step1Schema.extend(step2Schema.shape).extend(step3Schema.shape);
export type ReportLostItemFormValues = z.infer<typeof reportLostItemSchema>;

export const defaultReportLostItemValues: ReportLostItemFormValues = {
  title: "",
  category: "",
  description: "",
  dateLost: "",
  lastKnownLocation: "",
  hiddenInformation: "",
  photos: [],
};
