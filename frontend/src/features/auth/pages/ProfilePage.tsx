import { useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAuth } from "../AuthContext";
import { ProfileEditForm } from "../components/ProfileEditForm";
import { DeleteAccountSection } from "../components/DeleteAccountSection";
import { PossibleMatchesTile } from "../../matches/components/PossibleMatchesTile";

export function ProfilePage() {
  const navigate = useNavigate();
  const { user, isLoading, setUser } = useAuth();
  const [editing, setEditing] = useState(false);

  if (isLoading) {
    return (
      <div className="flex min-h-screen items-center justify-center">
        <div
          role="status"
          aria-label="Loading profile"
          className="h-8 w-8 animate-spin rounded-full border-4 border-indigo-600 border-t-transparent"
        />
      </div>
    );
  }

  if (!user) {
    return null;
  }

  const initials = user.name
    .split(" ")
    .map((name) => name[0])
    .join("")
    .toUpperCase()
    .slice(0, 2);

  const memberSince = new Date(
    user.createdAt,
  ).toLocaleDateString("en-US", {
    month: "short",
    year: "numeric",
  });

  return (
    <div className="min-h-screen bg-gray-50">
      <header className="bg-white shadow-sm">
        <div className="mx-auto flex max-w-7xl items-center justify-between px-4 py-4">
          <h1 className="text-xl font-bold text-indigo-600">
            back2u - lost and found system
          </h1>

          <div className="flex items-center gap-4">
            <span className="text-sm text-gray-600">
              {user.name}
            </span>

            <button
              type="button"
              onClick={() =>
                navigate(
                  user.isAdmin
                    ? "/admin/dashboard"
                    : "/",
                )
              }
              className="text-sm font-medium text-indigo-600 transition-colors hover:text-indigo-500"
            >
              Dashboard
            </button>
          </div>
        </div>
      </header>

      <main className="mx-auto max-w-xl px-4 py-12">
        {!user.isAdmin && user.isEmailVerified && (
          <PossibleMatchesTile key={user.email} />
        )}

        <div className="overflow-hidden rounded-xl border border-gray-200 bg-white shadow-sm">
          <div className="flex items-center gap-4 px-6 pb-4 pt-6">
            <div className="flex h-14 w-14 shrink-0 items-center justify-center rounded-full bg-indigo-600 text-lg font-bold text-white">
              {initials}
            </div>

            <div>
              <h2 className="text-lg font-semibold text-gray-900">
                {user.name}
              </h2>
              <p className="text-sm text-gray-500">
                {user.email}
              </p>
            </div>
          </div>

          <div className="border-t border-gray-100" />

          <div className="px-6 py-5">
            {editing ? (
              <ProfileEditForm
                user={user}
                onSaved={(updated) => {
                  setUser(updated);
                  setEditing(false);
                }}
                onCancel={() => setEditing(false)}
              />
            ) : (
              <dl className="space-y-4">
                <div>
                  <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                    Name
                  </dt>
                  <dd className="mt-1 text-sm text-gray-900">
                    {user.name}
                  </dd>
                </div>

                <div>
                  <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                    Email
                  </dt>

                  <dd className="mt-1 flex items-center gap-2 text-sm text-gray-900">
                    {user.email}

                    {user.isEmailVerified ? (
                      <span className="inline-flex items-center gap-1 rounded-full bg-emerald-50 px-2 py-0.5 text-xs font-medium text-emerald-700">
                        <svg
                          aria-hidden="true"
                          className="h-3 w-3"
                          viewBox="0 0 20 20"
                          fill="currentColor"
                        >
                          <path
                            fillRule="evenodd"
                            d="M16.704 4.153a.75.75 0 01.143 1.052l-8 10.5a.75.75 0 01-1.127.075l-4.5-4.5a.75.75 0 011.06-1.06l3.894 3.893 7.48-9.817a.75.75 0 011.05-.143z"
                            clipRule="evenodd"
                          />
                        </svg>
                        Verified
                      </span>
                    ) : (
                      <span className="inline-flex items-center gap-1 rounded-full bg-amber-50 px-2 py-0.5 text-xs font-medium text-amber-700">
                        <svg
                          aria-hidden="true"
                          className="h-3 w-3"
                          viewBox="0 0 20 20"
                          fill="currentColor"
                        >
                          <path
                            fillRule="evenodd"
                            d="M8.485 2.495c.673-1.167 2.357-1.167 3.03 0l6.28 10.875c.673 1.167-.17 2.625-1.516 2.625H3.72c-1.347 0-2.189-1.458-1.515-2.625L8.485 2.495zM10 5a.75.75 0 01.75.75v3.5a.75.75 0 01-1.5 0v-3.5A.75.75 0 0110 5zm0 9a1 1 0 100-2 1 1 0 000 2z"
                            clipRule="evenodd"
                          />
                        </svg>
                        Not verified
                      </span>
                    )}
                  </dd>
                </div>

                <div>
                  <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                    Phone
                  </dt>
                  <dd className="mt-1 text-sm text-gray-900">
                    {user.phoneNo}
                  </dd>
                </div>

                <div>
                  <dt className="text-xs font-medium uppercase tracking-wide text-gray-500">
                    Member since
                  </dt>
                  <dd className="mt-1 text-sm text-gray-900">
                    {memberSince}
                  </dd>
                </div>
              </dl>
            )}
          </div>

          {!editing && (
            <div className="px-6 pb-5">
              <button
                type="button"
                onClick={() => setEditing(true)}
                className="w-full rounded-lg border border-gray-300 bg-white px-4 py-2 text-sm font-medium text-gray-700 shadow-sm transition-colors hover:bg-gray-50"
              >
                Edit profile
              </button>
            </div>
          )}
        </div>

        {!user.isAdmin && (
          <div className="mt-8">
            <DeleteAccountSection />
          </div>
        )}
      </main>
    </div>
  );
}