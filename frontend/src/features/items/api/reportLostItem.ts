import { apiPostForm } from "../../../lib/apiClient";

export type ReportLostItemPayload = {
  title: string;
  category: string;
  description: string;
  dateLost: string;
  lastKnownLocation: string;
  hiddenInformation: string;
  photos?: File[];
};

export type LostItemResponse = {
  id: string;
  userId: string;
  title: string;
  category: string;
  description: string;
  dateLost: string;
  lastKnownLocation: string;
  status: string;
  photoUrls: string[];
  createdAt: string;
};

export const reportLostItem = (
  payload: ReportLostItemPayload,
  signal?: AbortSignal,
): Promise<LostItemResponse> => {
  const form = new FormData();
  form.append("Title", payload.title);
  form.append("Category", payload.category);
  form.append("Description", payload.description);
  form.append("DateLost", payload.dateLost);
  form.append("LastKnownLocation", payload.lastKnownLocation);
  form.append("HiddenInformation", payload.hiddenInformation);
  if (payload.photos) {
    payload.photos.forEach((file) => form.append("Photos", file));
  }

  return apiPostForm<LostItemResponse>("items", "/api/items/lost", form, signal);
};
