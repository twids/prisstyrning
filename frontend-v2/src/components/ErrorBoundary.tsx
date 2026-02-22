import { Component, ReactNode, ErrorInfo } from 'react';

interface Props { children: ReactNode; }
interface State { hasError: boolean; error?: Error; }

export default class ErrorBoundary extends Component<Props, State> {
  constructor(props: Props) {
    super(props);
    this.state = { hasError: false };
  }
  static getDerivedStateFromError(error: Error): State {
    return { hasError: true, error };
  }
  componentDidCatch(error: Error, errorInfo: ErrorInfo) {
    console.error('ErrorBoundary:', error, errorInfo);
  }
  handleReset = () => {
    this.setState({ hasError: false, error: undefined });
    window.location.href = '/v2';
  };
  render() {
    if (this.state.hasError) {
      return (
        <div className="min-h-screen flex items-center justify-center p-4">
          <div className="bg-white dark:bg-gray-900 rounded-xl border border-gray-200 dark:border-gray-800 p-8 max-w-md text-center">
            <div className="text-red-500 text-5xl mb-4">⚠</div>
            <h2 className="text-xl font-semibold mb-2">Something went wrong</h2>
            <p className="text-sm text-gray-600 dark:text-gray-400 mb-4">{this.state.error?.message || 'An unexpected error occurred'}</p>
            <button onClick={this.handleReset} className="px-4 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 transition-colors text-sm font-medium">
              Return to Dashboard
            </button>
          </div>
        </div>
      );
    }
    return this.props.children;
  }
}
