﻿//
// MSBuildProjectExtensions.cs
//
// Author:
//       Matt Ward <matt.ward@xamarin.com>
//
// Copyright (c) 2016 Xamarin Inc. (http://xamarin.com)
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.

using System.Linq;
using MonoDevelop.Projects.MSBuild;

namespace MonoDevelop.DotNetCore
{
	static class MSBuildProjectExtensions
	{
		public static MSBuildImport AddImport (
			this MSBuildProject project,
			string importedProjectFile,
			bool importAtTop = false,
			string condition = null)
		{
			MSBuildObject before = GetInsertBeforeObject (project, importAtTop);
			return project.AddNewImport (importedProjectFile, condition, before);
		}

		static MSBuildObject GetInsertBeforeObject (MSBuildProject project, bool importAtTop)
		{
			if (importAtTop) {
				return project.GetAllObjects ().FirstOrDefault ();
			}

			// Return an unknown MSBuildItem instead of null so the MSBuildProject adds the import as the last
			// child in the project.
			return new MSBuildItem ();
		}

		public static void AddPropertyBefore (this MSBuildProject project, string name, string value, MSBuildObject beforeItem)
		{
			var propertyGroup = project.CreatePropertyGroup ();
			propertyGroup.Condition = string.Format ("'$({0})' == ''", name);
			propertyGroup.SetValue (name,value);

			project.AddPropertyGroup (propertyGroup, false, beforeItem);
		}
	}
}
