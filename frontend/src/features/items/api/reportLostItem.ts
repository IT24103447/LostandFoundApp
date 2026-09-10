import { apiGet, apiPut, apiPutForm, apiPostForm, apiDelete } from "../../../lib/apiClient";
export type ItemPhoto = { id: string; url: string };

export type UpdateLostItemPayload = {
  title: string;
  category: string;
  description: string;
  dateLost: string;
  lastKnownLocation: string;
  hiddenInformation: string;
};

export const getLostItem = (id: string, signal?: AbortSignal): Promise<LostItemResponse> =>
  apiGet<LostItemResponse>("items", `/api/items/lost/${id}`, signal);

export const updateLostItem = (
  id: string,
  payload: UpdateLostItemPayload,
  signal?: AbortSignal,
): Promise<LostItemResponse> =>
  apiPut<UpdateLostItemPayload, LostItemResponse>("items", `/api/items/lost/${id}`, payload, signal);

export const replaceLostItemPhoto = (
  id: string,
  file: File,
  signal?: AbortSignal,
): Promise<LostItemResponse> => {
  const form = new FormData();
  form.append("photo", file);
  return apiPutForm<LostItemResponse>("items", `/api/items/lost/${id}/photo`, form, signal);
};

export const deleteLostItemPhoto = (
  id: string,
  signal?: AbortSignal,
): Promise<LostItemResponse> =>
  apiDelete<undefined, LostItemResponse>("items", `/api/items/lost/${id}/photo`, undefined, signal);


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
  photo: ItemPhoto | null;
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