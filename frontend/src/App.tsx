import { Routes, Route, Navigate } from "react-router-dom";
import { RegisterPage } from "./features/auth/pages/RegisterPage";
import { VerifyEmailPage } from "./features/auth/pages/VerifyEmailPage";
import { LoginPage } from "./features/auth/pages/LoginPage";
import { ForgotPasswordPage } from "./features/auth/pages/ForgotPasswordPage";
import { ResetPasswordPage } from "./features/auth/pages/ResetPasswordPage";
import { HomePage } from "./features/home/pages/HomePage";
import { AdminLayout } from "./features/admin/components/AdminLayout";
import { AdminDashboardPage } from "./features/admin/pages/AdminDashboardPage";
import { UserManagementSection } from "./features/admin/components/UserManagementSection";
import { AdminProfileSection } from "./features/admin/components/AdminProfileSection";
import { ProfilePage } from "./features/auth/pages/ProfilePage";
import { ProtectedRoute } from "./features/auth/components/ProtectedRoute";
import { ReportLostItemPage } from "./features/items/pages/ReportLostItemPage";
import { ReportLostItemSuccessPage } from "./features/items/pages/ReportLostItemSuccessPage";
import { ReportFoundItemPage } from "./features/items/pages/ReportFoundItemPage";
import { ReportFoundItemSuccessPage } from "./features/items/pages/ReportFoundItemSuccessPage";
import { MyReportsPage } from "./features/items/pages/MyReportsPage";
import { EditLostItemPage } from "./features/items/pages/EditLostItemPage";
import { EditFoundItemPage } from "./features/items/pages/EditFoundItemPage";

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route path="/register" element={<RegisterPage />} />
      <Route path="/verify-email" element={<VerifyEmailPage />} />
      <Route path="/forgot-password" element={<ForgotPasswordPage />} />
      <Route path="/reset-password" element={<ResetPasswordPage />} />

      <Route element={<ProtectedRoute />}>
        <Route path="/" element={<HomePage />} />
        <Route path="/home" element={<Navigate to="/" replace />} />
        <Route path="/profile" element={<ProfilePage />} />
        <Route path="/report-lost-item" element={<ReportLostItemPage />} />
        <Route path="/report-lost-item/success" element={<ReportLostItemSuccessPage />} />
        <Route path="/report-found-item" element={<ReportFoundItemPage />} />
        <Route path="/report-found-item/success" element={<ReportFoundItemSuccessPage />} />
        <Route path="/my-reports" element={<MyReportsPage />} />
        <Route path="/edit-lost-item/:id" element={<EditLostItemPage />} />
        <Route path="/edit-found-item/:id" element={<EditFoundItemPage />} />
      </Route>

      <Route element={<ProtectedRoute roles={["Admin"]} />}>
        <Route path="/admin" element={<AdminLayout />}>
          <Route index element={<AdminDashboardPage />} />
          <Route path="dashboard" element={<AdminDashboardPage />} />
          <Route path="users" element={<UserManagementSection />} />
          <Route path="profile" element={<AdminProfileSection />} />
        </Route>
      </Route>
    </Routes>
  );
}
