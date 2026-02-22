import { ReactNode } from 'react';

interface CardProps {
  children: ReactNode;
  className?: string;
}

export default function Card({ children, className = '' }: CardProps) {
  return (
    <div className={`bg-white dark:bg-gray-900 rounded-xl border border-gray-200 dark:border-gray-800 p-5 ${className}`}>
      {children}
    </div>
  );
}
