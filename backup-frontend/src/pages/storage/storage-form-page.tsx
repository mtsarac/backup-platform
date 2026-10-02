import { ArrowLeft } from 'lucide-react'
import { Link } from 'react-router-dom'
import { StorageForm } from '@/components/storage/storage-form'
import { ROUTES } from '@/lib/routes'

export function StorageFormPage() {
  return (
    <div className="space-y-4">
      <Link
        to={ROUTES.STORAGE}
        className="inline-flex items-center gap-1 text-sm font-medium text-primary hover:underline"
      >
        <ArrowLeft className="size-4" />
        Depolamaya dön
      </Link>

      <h1 className="text-2xl font-semibold text-text-heading">Depolama Ekle</h1>
      <StorageForm />
    </div>
  )
}
