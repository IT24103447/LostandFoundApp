import { apiPostForm } from "../../../lib/apiClient";

export type ReportFoundItemPayload = {
  title: string;
  category: string;
  description: string;
  dateFound: string;
  locationFound: string;
  hiddenInformation: string;
  photos?: File[];
};

// IMPORTANT: there is deliberately no `hiddenInformation` field on this type.
// The backend's FoundItemResponseDto never returns it (see
// FoundItemsController.ToDto on the backend), so it must never be modelled
// here either — that's what keeps it out of every frontend-facing response.
export type FoundItemResponse = {
  id: string;
  userId: string;
  title: string;
  category: string;
  description: string;
  dateFound: string;
  locationFound: string;
  status: string;
  photoUrls: string[];
  createdAt: string;
};

export const reportFoundItem = (
  payload: ReportFoundItemPayload,
  signal?: AbortSignal,
): Promise<FoundItemResponse> => {
  const form = new FormData();
  form.append("Title", payload.title);
  form.append("Category", payload.category);
  form.append("Description", payload.description);
  form.append("DateFound", payload.dateFound);
  form.append("LocationFound", payload.locationFound);
  form.append("HiddenInformation", payload.hiddenInformation);
  if (payload.photos) {
    payload.photos.forEach((file) => form.append("Photos", file));
  }

  return apiPostForm<FoundItemResponse>("items", "/api/items/found", form, signal);
};
