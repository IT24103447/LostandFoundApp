import { useEffect, useState } from "react";
import { resolvePhotoUrl } from "../../../config/env";
import { getItemDetails } from "../../items/api/browseItems";
import type { ClaimItem } from "../api/matches";

export function MatchItemCard({
  item,
}: {
  item: ClaimItem;
}) {
  const [photoUrl, setPhotoUrl] =
    useState<string | null>(null);

  useEffect(() => {
    const controller = new AbortController();

    setPhotoUrl(null);

    getItemDetails(item.id, controller.signal)
      .then((details) => {
        if (!controller.signal.aborted) {
          setPhotoUrl(
            details.photoUrls?.[0] ?? null,
          );
        }
      })
      .catch(() => {
        // A resolved or removed report may no longer expose its photo.
      });

    return () => controller.abort();
  }, [item.id]);

  return (
    <article className="overflow-hidden rounded-xl border border-gray-200 bg-white">
      {photoUrl ? (
        <img
          src={resolvePhotoUrl(photoUrl)}
          alt={item.title}
          className="h-40 w-full bg-gray-50 object-contain"
        />
      ) : (
        <div className="flex h-40 items-center justify-center bg-gray-50 text-sm text-gray-500">
          Photo unavailable
        </div>
      )}

      <div className="space-y-2 p-4">
        <p className="text-xs font-bold text-indigo-600">
          {item.type}
        </p>

        <h3 className="font-semibold text-gray-900">
          {item.title}
        </h3>

        <p className="text-sm text-gray-600">
          {item.category}
        </p>

        <p className="text-sm text-gray-600">
          {item.date}
        </p>

        <p className="text-sm text-gray-600">
          {item.location}
        </p>
      </div>
    </article>
  );
}