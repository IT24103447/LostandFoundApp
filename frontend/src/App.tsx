import { Routes, Route } from "react-router-dom";
import { RegisterPage } from "./features/auth/pages/RegisterPage";
import { VerifyEmailPage } from "./features/auth/pages/VerifyEmailPage";

export default function App() {
  return (
    <Routes>
      <Route path="" element={<RegisterPage />} />
      <Route path="verify-email" element={<VerifyEmailPage />} />
    </Routes>
  );
}
