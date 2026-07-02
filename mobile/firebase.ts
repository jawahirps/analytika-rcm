import { initializeApp } from "firebase/app";
import { getAnalytics } from "firebase/analytics";
import { Platform } from "react-native";

const firebaseConfig = {
  apiKey: "AIzaSyB6ESRPMmdEhsGkuMUchaXg146wgSvmzFU",
  authDomain: "bi-intel.firebaseapp.com",
  projectId: "bi-intel",
  storageBucket: "bi-intel.firebasestorage.app",
  messagingSenderId: "672898743480",
  appId: "1:672898743480:web:173ddd062587e0162b084d",
  measurementId: "G-9HPZH8KPJ1",
};

export const app = initializeApp(firebaseConfig);

// Analytics is only supported on Expo Web; skip it on iOS/Android
export const analytics = Platform.OS === "web" ? getAnalytics(app) : null;
