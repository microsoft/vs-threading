/********************************************************
*                                                        *
*   © Copyright (C) Microsoft. All rights reserved.      *
*                                                        *
*********************************************************/

namespace Microsoft.VisualStudio.Threading
{
    /// <summary>
    /// Represents a dependent item in the JoinableTask dependency graph, it can be either a <see cref="JoinableTask"/> or a <see cref="JoinableTaskCollection"/>
    /// </summary>
    internal interface IJoinableTaskDependent
    {
        /// <summary>
        /// Gets the <see cref="Threading.JoinableTaskContext"/> this node belongs to.
        /// </summary>
        JoinableTaskContext JoinableTaskContext { get; }

        /// <summary>
        /// Gets a value indicating whether we need reference count child dependent node.  This is to keep the current behavior of <see cref="JoinableTaskCollection"/>.
        /// </summary>
        bool NeedRefCountChildDependencies { get; }

        /// <summary>
        /// Get the reference of dependent node to record dependencies.
        /// </summary>
        ref JoinableTaskDependencyGraph.JoinableTaskDependentData GetJoinableTaskDependentData();

        /// <summary>
        /// A function is called, when this dependent node is added to be a dependency of a parent node.
        /// </summary>
        void OnAddedToDependency(IJoinableTaskDependent parent);

        /// <summary>
        /// A function is called, when this dependent node is removed as a dependency of a parent node.
        /// </summary>
        void OnRemovedFromDependency(IJoinableTaskDependent parentNode);

        /// <summary>
        /// A function is called, when a dependent child is added.
        /// </summary>
        void OnDependencyAdded(IJoinableTaskDependent joinChild);

        /// <summary>
        /// A function is called, when a dependent child is removed.
        /// </summary>
        void OnDependencyRemoved(IJoinableTaskDependent joinChild);
    }
}
