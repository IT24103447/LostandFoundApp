import { useEffect, useState } from "react";
import { resolvePhotoUrl } from "../../../config/env";
import { getItemDetails } from "../../items/api/browseItems";
import type { ClaimItem } from "../api/matches";

type Props = {
  item: ClaimItem;
  ownerLabel?: string;
  showDescription?: boolean;
};

export function MatchItemCard({
  item,
  ownerLabel,
  showDescription = false,
}: Props) {
  const [photoUrl, setPhotoUrl] = useState<string | null>(null);
  const [photoLoading, setPhotoLoading] = useState(true);
  const [photoFailed, setPhotoFailed] = useState(false);

  useEffect(() => {
    const controller = new AbortController();

    setPhotoUrl(null);
    setPhotoLoading(true);
    setPhotoFailed(false);

    // Match snapshots remain readable after the original report is resolved.
    if (item.photoSnapshotCaptured || item.photoUrl != null) {
      setPhotoUrl(item.photoUrl ?? null);
      setPhotoLoading(false);
      return () => controller.abort();
    }

    getItemDetails(item.id, controller.signal)
      .then((details) => {
        if (!controller.signal.aborted) {
          setPhotoUrl(details.photoUrls?.[0] ?? null);
        }
      })
      .catch(() => {
        if (!controller.signal.aborted) {
          setPhotoFailed(true);
        }
      })
      .finally(() => {
        if (!controller.signal.aborted) {
          setPhotoLoading(false);
        }
      });

    return () => controller.abort();
  }, [item.id, item.photoUrl, item.photoSnapshotCaptured]);

  const isLost = item.type === "LOST";

  return (
    <article
      className={`h-full overflow-hidden rounded-xl border-2 bg-white ${
        isLost ? "border-violet-200" : "border-orange-200"
      }`}
    >
      <div
        className={`flex flex-col items-start gap-2 px-4 py-3 text-left ${
          isLost ? "bg-violet-50" : "bg-orange-50"
        }`}
      >
        {ownerLabel && (
          <p className="font-bold text-gray-900">
            {ownerLabel}
          </p>
        )}

        <span
          className={`rounded-full px-3 py-1 text-xs font-extrabold tracking-wide ${
            isLost
              ? "bg-violet-700 text-white"
              : "bg-orange-700 text-white"
          }`}
        >
          {isLost ? "LOST REPORT" : "FOUND REPORT"}
        </span>
      </div>

      {photoUrl && !photoFailed ? (
        <img
          src={resolvePhotoUrl(photoUrl)}
          alt={item.title}
          loading="lazy"
          onError={() => setPhotoFailed(true)}
          className="h-44 w-full bg-gray-50 object-contain"
        />
      ) : (
        <div className="flex h-44 items-center justify-center bg-gray-50 text-sm text-gray-500">
          {photoLoading
            ? "Loading photo…"
            : photoFailed
              ? "Photo unavailable"
              : "No photo attached"}
        </div>
      )}

      <div className="space-y-3 p-4 text-left leading-relaxed">
        <h3 className="text-lg font-semibold text-gray-900">
          {item.title}
        </h3>

        <dl className="space-y-2 text-sm">
          <div>
            <dt className="font-medium text-gray-500">
              Category
            </dt>
            <dd className="text-gray-900">{item.category}</dd>
          </div>

          <div>
            <dt className="font-medium text-gray-500">
              {isLost ? "Date lost" : "Date found"}
            </dt>
            <dd className="text-gray-900">{item.date}</dd>
          </div>

          <div>
            <dt className="font-medium text-gray-500">
              {isLost ? "Last known location" : "Location found"}
            </dt>
            <dd className="text-gray-900">{item.location}</dd>
          </div>
        </dl>

        {showDescription && (
          <div className="border-t border-gray-100 pt-3">
            <p className="text-sm font-medium text-gray-500">
              Description
            </p>
            <p className="mt-1 whitespace-pre-wrap text-sm text-gray-700">
              {item.description || "No description provided."}
            </p>
          </div>
        )}
      </div>
    </article>
  );
}
